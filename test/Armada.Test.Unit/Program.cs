namespace Armada.Test.Unit
{
    using Armada.Test.Common;
    using Armada.Test.Unit.Suites.Database;
    using Armada.Test.Unit.Suites.Models;
    using Armada.Test.Unit.Suites.Services;

    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            bool noCleanup = args.Contains("--no-cleanup");
            HashSet<string> suiteFilters = args
                .Select((value, index) => new { value, index })
                .Where(entry => String.Equals(entry.value, "--suite", StringComparison.OrdinalIgnoreCase) && (entry.index + 1) < args.Length)
                .Select(entry => args[entry.index + 1].Trim())
                .Where(value => !String.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            TestRunner runner = new TestRunner("ARMADA UNIT TEST SUITE");
            void AddSuite(TestSuite suite)
            {
                if (suiteFilters.Count == 0 || suiteFilters.Contains(suite.Name))
                {
                    runner.AddSuite(suite);
                }
            }

            // Database tests
            AddSuite(new FleetDatabaseTests());
            AddSuite(new VesselDatabaseTests());
            AddSuite(new VesselTests());
            AddSuite(new CaptainDatabaseTests());
            AddSuite(new CaptainTests());
            AddSuite(new MissionDatabaseTests());
            AddSuite(new VoyageDatabaseTests());
            AddSuite(new DockDatabaseTests());
            AddSuite(new SignalDatabaseTests());
            AddSuite(new EventDatabaseTests());
            AddSuite(new RequestHistoryDatabaseTests());
            AddSuite(new EventTests());
            AddSuite(new EnumerationTests());
            AddSuite(new ForeignKeyTests());
            AddSuite(new ConcurrentAccessTests());
            AddSuite(new DatabaseInitializationTests());
            AddSuite(new PlanningSessionDatabaseTests());
            AddSuite(new SchemaMigrationTests());
            AddSuite(new EdgeCaseTests());
            AddSuite(new TenantMethodsTests());
            AddSuite(new UserMethodsTests());
            AddSuite(new CredentialMethodsTests());
            AddSuite(new TenantFencingTests());
            AddSuite(new EntityTenantScopedTests());
            AddSuite(new DefaultSeedingTests());
            AddSuite(new TenantScopedEnumerationTests());
            AddSuite(new TenantScopedPaginationTests());
            AddSuite(new TenantScopedPaginationTests2());

            // Model tests
            AddSuite(new FleetModelTests());
            AddSuite(new VesselModelTests());
            AddSuite(new CaptainModelTests());
            AddSuite(new MissionModelTests());
            AddSuite(new VoyageModelTests());
            AddSuite(new PlanningSessionModelTests());
            AddSuite(new DockModelTests());
            AddSuite(new SignalModelTests());
            AddSuite(new ArmadaEventModelTests());
            AddSuite(new ArmadaStatusModelTests());
            AddSuite(new EnumModelTests());
            AddSuite(new TenantMetadataTests());
            AddSuite(new UserMasterTests());
            AddSuite(new CredentialTests());
            AddSuite(new AuthContextTests());
            AddSuite(new ObjectiveModelTests());
            AddSuite(new ObjectiveRefinementModelTests());

            // Service tests
            AddSuite(new AdmiralServiceTests());
            AddSuite(new EntityResolverTests());
            AddSuite(new MessageTemplateServiceTests());
            AddSuite(new ProgressParserTests());
            AddSuite(new SettingsTests());
            AddSuite(new ReleaseVersionTests());
            AddSuite(new StartupScriptTests());
            AddSuite(new GitServiceTests());
            AddSuite(new GitInferenceTests());
            AddSuite(new LogRotationServiceTests());
            AddSuite(new DataExpiryServiceTests());
            AddSuite(new NotificationServiceTests());
            AddSuite(new RuntimeDetectionServiceTests());
            AddSuite(new RemoteTunnelManagerTests());
            AddSuite(new RemoteDashboardRelayServiceTests());
            AddSuite(new ProxyAuthServiceTests());
            AddSuite(new ProxyRegistryTests());
            AddSuite(new ProxyRoutePolicyServiceTests());
            AddSuite(new ProxyDashboardRelayIntegrationTests());
            AddSuite(new CaptainServiceTests());
            AddSuite(new AgentLifecycleHandlerTests());
            AddSuite(new PlanningSessionCoordinatorTests());
            AddSuite(new MissionPromptTests());
            AddSuite(new SequentialDispatchTests());
            AddSuite(new MissionStatusTransitionTests());
            AddSuite(new LandingPipelineTests());
            AddSuite(new SessionTokenServiceTests());
            AddSuite(new AuthenticationServiceTests());
            AddSuite(new AuthorizationConfigTests());
            AddSuite(new AuthorizationServiceTests());
            AddSuite(new AuthEndpointTests());
            AddSuite(new PromptTemplateServiceTests());
            AddSuite(new PromptSignalConsistencyTests());
            AddSuite(new PersonaPipelineDbTests());
            AddSuite(new PipelineDispatchTests());
            AddSuite(new ReviewGateTests());
            AddSuite(new WorkspaceServiceTests());
            AddSuite(new RequestHistoryCaptureServiceTests());
            AddSuite(new HistoricalTimelineServiceTests());
            AddSuite(new ObjectiveServiceTests());
            AddSuite(new ObjectiveRefinementCoordinatorTests());
            AddSuite(new IncidentServiceTests());
            AddSuite(new WorkflowProfileCheckRunServiceTests());
            AddSuite(new DeploymentEnvironmentServiceTests());
            AddSuite(new DeploymentServiceTests());
            AddSuite(new ReleaseServiceTests());

            int exitCode = await runner.RunAllAsync().ConfigureAwait(false);
            return exitCode;
        }
    }
}
