namespace Armada.Test.Unit
{
    using Armada.Test.Common;
    using Armada.Test.Unit.Suites.Database;
    using Armada.Test.Unit.Suites.Models;
    using Armada.Test.Unit.Suites.Recovery;
    using Armada.Test.Unit.Suites.Routes;
    using Armada.Test.Unit.Suites.Services;

    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            bool noCleanup = args.Contains("--no-cleanup");

            TestRunner runner = new TestRunner("ARMADA UNIT TEST SUITE");

            // Database tests
            runner.AddSuite(new FleetDatabaseTests());
            runner.AddSuite(new VesselDatabaseTests());
            runner.AddSuite(new VesselTests());
            runner.AddSuite(new CaptainDatabaseTests());
            runner.AddSuite(new CaptainTests());
            runner.AddSuite(new MissionDatabaseTests());
            runner.AddSuite(new VoyageDatabaseTests());
            runner.AddSuite(new DockDatabaseTests());
            runner.AddSuite(new SignalDatabaseTests());
            runner.AddSuite(new EventDatabaseTests());
            runner.AddSuite(new EventTests());
            runner.AddSuite(new EnumerationTests());
            runner.AddSuite(new ForeignKeyTests());
            runner.AddSuite(new ConcurrentAccessTests());
            runner.AddSuite(new DatabaseInitializationTests());
            runner.AddSuite(new PlanningSessionDatabaseTests());
            runner.AddSuite(new SchemaMigrationTests());
            runner.AddSuite(new SchemaMigrationV40ReflectionTests());
            runner.AddSuite(new SchemaMigrationV41ReorganizeTests());
            runner.AddSuite(new SchemaMigrationV42PipelineStageParallelTests());
            runner.AddSuite(new SchemaMigrationV38RoundTripTests());
            runner.AddSuite(new EdgeCaseTests());
            runner.AddSuite(new TenantMethodsTests());
            runner.AddSuite(new UserMethodsTests());
            runner.AddSuite(new CredentialMethodsTests());
            runner.AddSuite(new TenantFencingTests());
            runner.AddSuite(new EntityTenantScopedTests());
            runner.AddSuite(new DefaultSeedingTests());
            runner.AddSuite(new TenantScopedEnumerationTests());
            runner.AddSuite(new TenantScopedPaginationTests());
            runner.AddSuite(new TenantScopedPaginationTests2());

            // Model tests
            runner.AddSuite(new FleetModelTests());
            runner.AddSuite(new VesselModelTests());
            runner.AddSuite(new CaptainModelTests());
            runner.AddSuite(new MissionModelTests());
            runner.AddSuite(new VoyageModelTests());
            runner.AddSuite(new PlanningSessionModelTests());
            runner.AddSuite(new DockModelTests());
            runner.AddSuite(new SignalModelTests());
            runner.AddSuite(new ArmadaEventModelTests());
            runner.AddSuite(new ArmadaStatusModelTests());
            runner.AddSuite(new EnumModelTests());
            runner.AddSuite(new TenantMetadataTests());
            runner.AddSuite(new UserMasterTests());
            runner.AddSuite(new CredentialTests());
            runner.AddSuite(new AuthContextTests());

            // Service tests
            runner.AddSuite(new AdmiralServiceTests());
            runner.AddSuite(new MemoryHotfixRegressionTests());
            runner.AddSuite(new EntityResolverTests());
            runner.AddSuite(new MessageTemplateServiceTests());
            runner.AddSuite(new ProgressParserTests());
            runner.AddSuite(new SettingsTests());
            runner.AddSuite(new ReleaseVersionTests());
            runner.AddSuite(new StartupScriptTests());
            runner.AddSuite(new GitServiceTests());
            runner.AddSuite(new GitServiceIsPrMergedTests());
            runner.AddSuite(new GitInferenceTests());
            runner.AddSuite(new LogRotationServiceTests());
            runner.AddSuite(new DataExpiryServiceTests());
            runner.AddSuite(new NotificationServiceTests());
            runner.AddSuite(new RuntimeDetectionServiceTests());
            runner.AddSuite(new RemoteTunnelManagerTests());
            runner.AddSuite(new ProxyRegistryTests());
            runner.AddSuite(new RemoteControlQueryServiceTests());
            runner.AddSuite(new RemoteControlManagementServiceTests());
            runner.AddSuite(new CaptainServiceTests());
            runner.AddSuite(new AgentLifecycleHandlerTests());
            runner.AddSuite(new PlanningSessionCoordinatorTests());
            runner.AddSuite(new MissionPromptTests());
            runner.AddSuite(new SequentialDispatchTests());
            runner.AddSuite(new MissionStatusTransitionTests());
            runner.AddSuite(new LandingPipelineTests());
            runner.AddSuite(new SessionTokenServiceTests());
            runner.AddSuite(new AuthenticationServiceTests());
            runner.AddSuite(new AuthorizationConfigTests());
            runner.AddSuite(new AuthorizationServiceTests());
            runner.AddSuite(new AuthEndpointTests());
            runner.AddSuite(new PromptTemplateServiceTests());
            runner.AddSuite(new PromptSignalConsistencyTests());
            runner.AddSuite(new PersonaSeedServiceTests());
            runner.AddSuite(new PersonaPipelineDbTests());
            runner.AddSuite(new PipelineDispatchTests());
            runner.AddSuite(new PerStagePreferredModelTests());
            runner.AddSuite(new AliasPipelineDispatchTests());
            runner.AddSuite(new CrossVesselDependencyTests());
            runner.AddSuite(new CancelKillsCaptainTests());
            runner.AddSuite(new PrFallbackPersistenceTests());
            runner.AddSuite(new PrFallbackUnblockTests());
            runner.AddSuite(new JudgeHybridFollowUpsTests());
            runner.AddSuite(new DependsOnMissionIdDispatchTests());
            runner.AddSuite(new AutoLandEvaluatorTests());
            runner.AddSuite(new ConventionCheckTests());
            runner.AddSuite(new CriticalTriggerEvaluatorTests());
            runner.AddSuite(new AutoLandLandingHandlerTests());
            runner.AddSuite(new AutoLandSafetyNetIntegrationTests());
            runner.AddSuite(new AutoLandCalibrationTests());
            runner.AddSuite(new PrestagedFilesTests());
            runner.AddSuite(new ProtectedPathsTests());
            runner.AddSuite(new VesselAutoLandPredicateRoutesTests());
            runner.AddSuite(new VesselDefaultPlaybooksTests());
            runner.AddSuite(new AuditDrainerTests());
            runner.AddSuite(new ReflectionAuditDrainTests());
            runner.AddSuite(new ArchitectOutputParserTests());
            runner.AddSuite(new ReflectionOutputParserTests());
            runner.AddSuite(new McpArchitectToolsTests());
            runner.AddSuite(new ArchitectPersonaSyncTests());
            runner.AddSuite(new RemoteTriggerSettingsTests());
            runner.AddSuite(new RemoteTriggerHttpClientTests());
            runner.AddSuite(new RemoteTriggerServiceTests());
            runner.AddSuite(new RemoteTriggerEventHookTests());
            runner.AddSuite(new MergeQueueBranchCleanupTests());
            runner.AddSuite(new MergeQueueServiceClassificationTests());
            runner.AddSuite(new MergeFailureClassifierTests());
            runner.AddSuite(new RebaseCaptainDockSetupTests());
            runner.AddSuite(new MergeRecoveryHandlerRebasePathTests());
            runner.AddSuite(new RecoveryExhaustionFlowTests());
            runner.AddSuite(new AutoRecoveryEndToEndSmokeTests());
            runner.AddSuite(new PlaybookMergeTests());
            runner.AddSuite(new PullRequestServiceTests());
            runner.AddSuite(new VoyageMissionPlaybookPropagationTests());
            runner.AddSuite(new MissionAliasResolverTests());
            runner.AddSuite(new CodeIndexServiceTests());
            runner.AddSuite(new CodeIndexSettingsClampTests());
            runner.AddSuite(new DeepSeekEmbeddingClientTests());
            runner.AddSuite(new DeepSeekInferenceClientTests());
            runner.AddSuite(new CodeIndexProductionWiringTests());
            runner.AddSuite(new McpCodeIndexToolsTests());
            runner.AddSuite(new McpCaptainDiagnosticsToolsTests());
            runner.AddSuite(new McpStdioServerTests());
            runner.AddSuite(new McpVoyageToolsTests());
            runner.AddSuite(new McpMissionToolsTests());
            runner.AddSuite(new PreferredModelTierSelectorTests());
            runner.AddSuite(new PreferredModelUserGuidanceTests());
            runner.AddSuite(new MissionServicePreferredModelRoutingTests());
            runner.AddSuite(new SchedulerHydrationTests());
            runner.AddSuite(new ReflectionMemoryBootstrapServiceTests());
            runner.AddSuite(new ReflectionConsolidateToolsTests());
            runner.AddSuite(new ReflectionAcceptRejectToolsTests());
            runner.AddSuite(new ReflectionsEndToEndSmokeTests());
            runner.AddSuite(new ReflectionsV2F4EndToEndSmokeTests());
            runner.AddSuite(new ReflectionsV2F1EndToEndSmokeTests());
            runner.AddSuite(new ReflectionsV2F2EndToEndSmokeTests());
            runner.AddSuite(new ReflectionsV2F3EndToEndSmokeTests());
            runner.AddSuite(new VesselReflectionThresholdRoutesTests());
            runner.AddSuite(new VesselReorganizeThresholdRoutesTests());

            int exitCode = await runner.RunAllAsync().ConfigureAwait(false);
            return exitCode;
        }
    }
}
