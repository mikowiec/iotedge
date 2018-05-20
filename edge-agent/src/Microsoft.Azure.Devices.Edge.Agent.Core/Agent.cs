// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.Devices.Edge.Agent.Core
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Edge.Agent.Core.ConfigSources;
    using Microsoft.Azure.Devices.Edge.Agent.Core.Serde;
    using Microsoft.Azure.Devices.Edge.Storage;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Edge.Util.Concurrency;
    using Microsoft.Extensions.Logging;

    public class Agent
    {
        const string StoreConfigKey = "CurrentConfig";
        IEnvironment environment;
        readonly IPlanner planner;
        readonly IPlanRunner planRunner;
        readonly IReporter reporter;
        readonly IConfigSource configSource;
        readonly IModuleIdentityLifecycleManager moduleIdentityLifecycleManager;
        readonly IEntityStore<string, string> configStore;
        readonly IEnvironmentProvider environmentProvider;
        readonly AsyncLock reconcileLock = new AsyncLock();
        readonly ISerde<DeploymentConfigInfo> deploymentConfigInfoSerde;
        DeploymentConfigInfo currentConfig;

        public Agent(IConfigSource configSource, IEnvironmentProvider environmentProvider,
            IPlanner planner, IPlanRunner planRunner, IReporter reporter,
            IModuleIdentityLifecycleManager moduleIdentityLifecycleManager,
            IEntityStore<string, string> configStore, DeploymentConfigInfo initialDeployedConfigInfo,
            ISerde<DeploymentConfigInfo> deploymentConfigInfoSerde)
        {
            this.configSource = Preconditions.CheckNotNull(configSource, nameof(configSource));
            this.planner = Preconditions.CheckNotNull(planner, nameof(planner));
            this.planRunner = Preconditions.CheckNotNull(planRunner, nameof(planRunner));
            this.reporter = Preconditions.CheckNotNull(reporter, nameof(reporter));
            this.moduleIdentityLifecycleManager = Preconditions.CheckNotNull(moduleIdentityLifecycleManager, nameof(moduleIdentityLifecycleManager));
            this.configStore = Preconditions.CheckNotNull(configStore, nameof(configStore));
            this.environmentProvider = Preconditions.CheckNotNull(environmentProvider, nameof(environmentProvider));
            this.currentConfig = Preconditions.CheckNotNull(initialDeployedConfigInfo);
            this.deploymentConfigInfoSerde = Preconditions.CheckNotNull(deploymentConfigInfoSerde, nameof(deploymentConfigInfoSerde));
            this.environment = this.environmentProvider.Create(this.currentConfig.DeploymentConfig);
            Events.AgentCreated();
        }

        public static async Task<Agent> Create(IConfigSource configSource, IPlanner planner, IPlanRunner planRunner, IReporter reporter,
            IModuleIdentityLifecycleManager moduleIdentityLifecycleManager, IEnvironmentProvider environmentProvider,
            IEntityStore<string, string> configStore, ISerde<DeploymentConfigInfo> deploymentConfigInfoSerde)
        {
            Preconditions.CheckNotNull(deploymentConfigInfoSerde, nameof(deploymentConfigInfoSerde));
            Preconditions.CheckNotNull(configStore, nameof(configStore));

            Option<DeploymentConfigInfo> deploymentConfigInfo = Option.None<DeploymentConfigInfo>();
            try
            {
                Option<string> deploymentConfigInfoJson = await Preconditions.CheckNotNull(configStore, nameof(configStore)).Get(StoreConfigKey);
                deploymentConfigInfo = deploymentConfigInfoJson.Map(json => deploymentConfigInfoSerde.Deserialize(json));
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                Events.ErrorDeserializingConfig(ex);
            }
            var agent = new Agent(configSource, environmentProvider, planner, planRunner, reporter, moduleIdentityLifecycleManager,
                configStore, deploymentConfigInfo.GetOrElse(DeploymentConfigInfo.Empty), deploymentConfigInfoSerde);
            return agent;
        }

        async Task<(ModuleSet current, Exception ex)> GetCurrentModuleSetAsync(CancellationToken token)
        {
            ModuleSet current = null;
            Exception ex = null;

            try
            {
                current = await this.environment.GetModulesAsync(token);
            }
            catch (Exception e) when (!ex.IsFatal())
            {
                ex = e;
            }

            return (current, ex);
        }

        async Task<(DeploymentConfigInfo deploymentConfigInfo, Exception ex)> GetDeploymentConfigInfoAsync()
        {
            DeploymentConfigInfo deploymentConfigInfo = null;
            Exception ex = null;

            try
            {
                Events.GettingDeploymentConfigInfo();
                deploymentConfigInfo = await this.configSource.GetDeploymentConfigInfoAsync();
                Events.ObtainedDeploymentConfigInfo(deploymentConfigInfo);
            }
            catch (Exception e) when (!ex.IsFatal())
            {
                ex = e;
            }

            return (deploymentConfigInfo, ex);
        }

        async Task<(ModuleSet current, DeploymentConfigInfo DeploymentConfigInfo, Exception ex)> GetReconcileData(CancellationToken token)
        {
            // we read the data from the config source and from the environment separately because
            // when doing something like TaskEx.WhenAll(t1, t2) if either of them throws then we get
            // nothing; so for example, if the environment is able to successfully retrieve the moduleset
            // but there's a corrupt deployment in IoT Hub then we end up not being able to report the
            // current state even though we have it

            ((ModuleSet current, Exception environmentException), (DeploymentConfigInfo deploymentConfigInfo, Exception configSourceException)) = await TaskEx.WhenAll(
                this.GetCurrentModuleSetAsync(token), this.GetDeploymentConfigInfoAsync()
            );

            List<Exception> exceptions = new[]
            {
                environmentException,
                configSourceException,
                deploymentConfigInfo?.Exception.OrDefault()
            }
            .Where(e => e != null)
            .ToList();

            Exception exception = null;
            if (exceptions.Any())
            {
                exception = exceptions.Count > 1 ? new AggregateException(exceptions) : exceptions[0];
            }

            return (current, deploymentConfigInfo, exception);
        }

        public async Task ReconcileAsync(CancellationToken token)
        {
            DeploymentStatus status = DeploymentStatus.Success;
            ModuleSet moduleSetToReport = null;
            using (await this.reconcileLock.LockAsync(token))
            {
                try
                {
                    (ModuleSet current, DeploymentConfigInfo deploymentConfigInfo, Exception exception) = await this.GetReconcileData(token);
                    moduleSetToReport = current;
                    if (exception != null)
                    {
                        throw exception;
                    }

                    DeploymentConfig deploymentConfig = deploymentConfigInfo.DeploymentConfig;
                    if (deploymentConfig != DeploymentConfig.Empty)
                    {
                        ModuleSet desiredModuleSet = deploymentConfig.GetModuleSet();
                        IImmutableDictionary<string, IModuleIdentity> identities = await this.moduleIdentityLifecycleManager.GetModuleIdentitiesAsync(desiredModuleSet, current);
                        Plan plan = await this.planner.PlanAsync(desiredModuleSet, current, deploymentConfig.Runtime, identities);
                        if (!plan.IsEmpty)
                        {
                            try
                            {
                                await this.planRunner.ExecuteAsync(deploymentConfigInfo.Version, plan, token);
                                await this.UpdateCurrentConfig(deploymentConfigInfo);
                            }
                            catch (Exception ex) when (!ex.IsFatal())
                            {
                                Events.PlanExecutionFailed(ex);
                                await this.UpdateCurrentConfig(deploymentConfigInfo);
                                throw;
                            }

                        }
                    }
                }
                catch (Exception ex) when (!ex.IsFatal())
                {
                    switch (ex)
                    {
                        case ConfigEmptyException _:
                            status = new DeploymentStatus(DeploymentStatusCode.ConfigEmptyError, ex.Message);
                            Events.EmptyConfig(ex);
                            break;

                        case InvalidSchemaVersionException _:
                            status = new DeploymentStatus(DeploymentStatusCode.InvalidSchemaVersion, ex.Message);
                            Events.InvalidSchemaVersion(ex);
                            break;

                        case ConfigFormatException _:
                            status = new DeploymentStatus(DeploymentStatusCode.ConfigFormatError, ex.Message);
                            Events.InvalidConfigFormat(ex);
                            break;

                        default:
                            status = new DeploymentStatus(DeploymentStatusCode.Failed, ex.Message);
                            Events.UnknownFailure(ex);
                            break;
                    }
                }
                await this.reporter.ReportAsync(token, moduleSetToReport, await this.environment.GetRuntimeInfoAsync(), this.currentConfig.Version, status);
            }
        }

        // This should be called only within the reconcile lock. 
        private async Task UpdateCurrentConfig(DeploymentConfigInfo deploymentConfigInfo)
        {
            this.environment = this.environmentProvider.Create(deploymentConfigInfo.DeploymentConfig);
            this.currentConfig = deploymentConfigInfo;
            await this.configStore.Put(StoreConfigKey, this.deploymentConfigInfoSerde.Serialize(deploymentConfigInfo));
        }

        public async Task ReportShutdownAsync(CancellationToken token)
        {
            try
            {
                var status = new DeploymentStatus(DeploymentStatusCode.Unknown, "Agent is not running");

                await this.reporter.ReportShutdown(status, token);
                Events.ReportShutdown();
            }
            catch (Exception ex) when (!ex.IsFatal())
            {
                Events.ReportShutdownFailed(ex);
                throw;
            }
        }

        static class Events
        {
            static readonly ILogger Log = Logger.Factory.CreateLogger<Agent>();
            const int IdStart = AgentEventIds.Agent;

            enum EventIds
            {
                AgentCreated = IdStart,
                PlanExecutionFailed,
                EmptyConfig,
                InvalidSchemaVersion,
                InvalidConfigFormat,
                ReportShutdown,
                ReportShutdownFailed,
                GettingDeploymentConfigInfo,
                ObtainedDeploymentConfigInfo,
                UnknownFailure,
                ErrorDeserializingConfig
            }

            public static void AgentCreated()
            {
                Log.LogDebug((int)EventIds.AgentCreated, "Edge agent created.");
            }

            public static void PlanExecutionFailed(Exception ex)
            {
                Log.LogError((int)EventIds.PlanExecutionFailed, ex, "Edge agent plan execution failed.");
            }

            public static void EmptyConfig(Exception ex)
            {
                Log.LogDebug((int)EventIds.EmptyConfig, ex.Message);
            }

            public static void InvalidSchemaVersion(Exception ex)
            {
                Log.LogWarning((int)EventIds.InvalidSchemaVersion, ex.Message);
            }

            public static void InvalidConfigFormat(Exception ex)
            {
                Log.LogWarning((int)EventIds.InvalidConfigFormat, ex.Message);
            }

            public static void UnknownFailure(Exception ex)
            {
                Log.LogWarning((int)EventIds.UnknownFailure, ex.Message);
            }

            public static void ReportShutdown()
            {
                Log.LogDebug((int)EventIds.ReportShutdown, "Edge agent reporting Edge and module state as unknown.");
            }

            public static void ReportShutdownFailed(Exception ex)
            {
                Log.LogError((int)EventIds.ReportShutdownFailed, ex, "Failed to report edge agent shutdown.");
            }

            public static void GettingDeploymentConfigInfo()
            {
                Log.LogDebug((int)EventIds.GettingDeploymentConfigInfo, "Getting edge agent config...");
            }

            public static void ObtainedDeploymentConfigInfo(DeploymentConfigInfo deploymentConfigInfo)
            {
                if (!deploymentConfigInfo.Exception.HasValue && deploymentConfigInfo != DeploymentConfigInfo.Empty)
                {
                    Log.LogDebug((int)EventIds.ObtainedDeploymentConfigInfo, "Obtained edge agent config");
                }
            }

            internal static void ErrorDeserializingConfig(Exception ex)
            {
                Log.LogWarning((int)EventIds.ErrorDeserializingConfig, ex, "There was an error deserializing stored deployment configuration information");
            }
        }
    }
}
