namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Covers report-only dispatch: a Research objective must run its missions read-only however it is
    /// dispatched, so its Judge accepts a sound no-commit report instead of failing it for an empty
    /// diff. The autonomous scheduler already derived the mode from the objective Kind; the residual
    /// bug was that the operator dispatch path did not, so a Research objective linked to an
    /// <c>armada_dispatch</c> produced Implementation missions on an Implementation pipeline. These
    /// tests pin the single shared derivation rule, the operator path deriving from the linked
    /// objective, and the pipeline dropping the diff-dependent Test Engineer stage while keeping a
    /// read-only Judge.
    /// </summary>
    public sealed class DispatchObjectiveModeTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Dispatch Objective Mode Derivation";

        /// <summary>Run the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("FromObjectiveKind: Research is read-only, every other Kind is the Implementation default", () =>
            {
                AssertEqual("Research", MissionModes.FromObjectiveKind(ObjectiveKindEnum.Research),
                    "A Research objective must derive the read-only Research mode.");
                AssertNull(MissionModes.FromObjectiveKind(ObjectiveKindEnum.Feature), "Feature keeps the Implementation default.");
                AssertNull(MissionModes.FromObjectiveKind(ObjectiveKindEnum.Bug), "Bug keeps the Implementation default.");
                AssertNull(MissionModes.FromObjectiveKind(ObjectiveKindEnum.Refactor), "Refactor keeps the Implementation default.");
                AssertNull(MissionModes.FromObjectiveKind(ObjectiveKindEnum.Chore), "Chore keeps the Implementation default.");
                AssertNull(MissionModes.FromObjectiveKind(ObjectiveKindEnum.Initiative), "Initiative keeps the Implementation default.");
                return Task.CompletedTask;
            });

            await RunTest("The scheduler and operator paths share one derivation rule", () =>
            {
                // The scheduler helper must delegate to the shared rule, so a Research objective is
                // judged the same way whether the scheduler or an operator dispatched it.
                AssertEqual(MissionModes.FromObjectiveKind(ObjectiveKindEnum.Research),
                    AutonomousObjectiveScheduler.DeriveMissionMode(ObjectiveKindEnum.Research),
                    "The scheduler derivation must match the shared rule for Research.");
                AssertEqual(MissionModes.FromObjectiveKind(ObjectiveKindEnum.Feature),
                    AutonomousObjectiveScheduler.DeriveMissionMode(ObjectiveKindEnum.Feature),
                    "The scheduler derivation must match the shared rule for Feature.");
                return Task.CompletedTask;
            });

            await RunTest("Operator dispatch of a Research objective runs its unset-mode missions read-only", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ServiceHarness harness = await ServiceHarness.CreateAsync(testDb).ConfigureAwait(false);
                    Objective research = await CreateObjectiveAsync(testDb, "Report on the login flow", ObjectiveKindEnum.Research).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription("Survey the login flow", "Inspect the repository and report findings.")
                    };
                    VoyageDispatchService service = harness.NewDispatchService();
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "research dispatch",
                        VesselId = harness.Vessel.Id,
                        CodeContextMode = "off",
                        ObjectiveId = research.Id,
                        Missions = missions
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);
                    AssertTrue(result.Succeeded, "research dispatch should succeed");

                    List<Mission> created = await testDb.Driver.Missions.EnumerateByVoyageAsync(result.Voyage!.Id).ConfigureAwait(false);
                    AssertEqual(1, created.Count, "one mission should be created");
                    AssertEqual(MissionModeEnum.Research, created[0].Mode,
                        "a Research objective must make its unset-mode mission read-only so the Judge accepts a no-commit report");
                    AssertTrue(created[0].IsReadOnlyMode, "the created mission must be read-only");
                }
            });

            await RunTest("Operator dispatch of a non-Research objective keeps the Implementation default", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ServiceHarness harness = await ServiceHarness.CreateAsync(testDb).ConfigureAwait(false);
                    Objective feature = await CreateObjectiveAsync(testDb, "Add the login flow", ObjectiveKindEnum.Feature).ConfigureAwait(false);

                    VoyageDispatchService service = harness.NewDispatchService();
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "feature dispatch",
                        VesselId = harness.Vessel.Id,
                        CodeContextMode = "off",
                        ObjectiveId = feature.Id,
                        Missions = new List<MissionDescription> { new MissionDescription("Build it", "Implement the feature.") }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);
                    AssertTrue(result.Succeeded, "feature dispatch should succeed");

                    List<Mission> created = await testDb.Driver.Missions.EnumerateByVoyageAsync(result.Voyage!.Id).ConfigureAwait(false);
                    AssertEqual(1, created.Count, "one mission should be created");
                    AssertEqual(MissionModeEnum.Implementation, created[0].Mode,
                        "a Feature objective must keep the Implementation default");
                }
            });

            await RunTest("An explicit mission mode wins over the linked objective Kind", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ServiceHarness harness = await ServiceHarness.CreateAsync(testDb).ConfigureAwait(false);
                    Objective research = await CreateObjectiveAsync(testDb, "Report, but this mission implements", ObjectiveKindEnum.Research).ConfigureAwait(false);

                    VoyageDispatchService service = harness.NewDispatchService();
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "explicit-mode dispatch",
                        VesselId = harness.Vessel.Id,
                        CodeContextMode = "off",
                        ObjectiveId = research.Id,
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("Implement anyway", "The operator set this mission to implement.")
                            {
                                Mode = MissionModeEnum.Implementation.ToString()
                            }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);
                    AssertTrue(result.Succeeded, "explicit-mode dispatch should succeed");

                    List<Mission> created = await testDb.Driver.Missions.EnumerateByVoyageAsync(result.Voyage!.Id).ConfigureAwait(false);
                    AssertEqual(1, created.Count, "one mission should be created");
                    AssertEqual(MissionModeEnum.Implementation, created[0].Mode,
                        "an explicit per-mission mode must win over the objective-derived mode");
                }
            });

            await RunTest("A Research mission on a Tested pipeline drops the Test Engineer and keeps a read-only Judge", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ServiceHarness harness = await ServiceHarness.CreateAsync(testDb).ConfigureAwait(false);
                    Pipeline tested = await CreateTestedPipelineAsync(testDb).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription("Report through the pipeline", "Inspect and report.")
                        {
                            Mode = MissionModeEnum.Research.ToString()
                        }
                    };

                    Voyage voyage = await harness.Admiral.DispatchVoyageAsync(
                        "research pipeline voyage", "report only", harness.Vessel.Id, missions, tested.Id).ConfigureAwait(false);

                    List<Mission> created = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    List<string?> personas = created.Select(m => m.Persona).ToList();

                    AssertFalse(personas.Contains("TestEngineer"),
                        "a read-only mission must drop the diff-dependent Test Engineer stage");
                    AssertTrue(personas.Contains("Worker"), "the Worker stage must remain");
                    AssertTrue(personas.Contains("Judge"), "the reviewing Judge stage must remain so the report is still judged");
                    AssertTrue(created.All(m => m.IsReadOnlyMode),
                        "every materialized stage of a Research mission must be read-only");
                }
            });

            await RunTest("An Implementation mission on a Tested pipeline keeps all three stages", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ServiceHarness harness = await ServiceHarness.CreateAsync(testDb).ConfigureAwait(false);
                    Pipeline tested = await CreateTestedPipelineAsync(testDb).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription("Implement through the pipeline", "Change code and cover it.")
                    };

                    Voyage voyage = await harness.Admiral.DispatchVoyageAsync(
                        "implementation pipeline voyage", "implement", harness.Vessel.Id, missions, tested.Id).ConfigureAwait(false);

                    List<Mission> created = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    List<string?> personas = created.Select(m => m.Persona).ToList();

                    AssertTrue(personas.Contains("Worker"), "the Worker stage must be present");
                    AssertTrue(personas.Contains("TestEngineer"), "an Implementation mission keeps its Test Engineer stage");
                    AssertTrue(personas.Contains("Judge"), "the Judge stage must be present");
                    AssertTrue(created.All(m => !m.IsReadOnlyMode), "every Implementation stage must keep the diff-review gate");
                }
            });
        }

        private static async Task<Objective> CreateObjectiveAsync(TestDatabase testDb, string title, ObjectiveKindEnum kind)
        {
            Objective objective = new Objective
            {
                Title = title,
                Kind = kind,
                TenantId = Constants.DefaultTenantId,
                UserId = Constants.DefaultUserId
            };
            return await testDb.Driver.Objectives.CreateAsync(objective).ConfigureAwait(false);
        }

        private static async Task<Pipeline> CreateTestedPipelineAsync(TestDatabase testDb)
        {
            Pipeline pipeline = new Pipeline("Tested");
            pipeline.Stages = new List<PipelineStage>
            {
                new PipelineStage(1, "Worker"),
                new PipelineStage(2, "TestEngineer"),
                new PipelineStage(3, "Judge")
            };
            return await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);
        }

        private sealed class ServiceHarness
        {
            public LoggingModule Logging { get; private set; } = null!;
            public ArmadaSettings Settings { get; private set; } = null!;
            public AdmiralService Admiral { get; private set; } = null!;
            public ObjectiveService Objectives { get; private set; } = null!;
            public Vessel Vessel { get; private set; } = null!;

            private TestDatabase _TestDb = null!;

            public static async Task<ServiceHarness> CreateAsync(TestDatabase testDb)
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;

                ArmadaSettings settings = new ArmadaSettings();
                settings.CodeIndex.Enabled = false;
                settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));

                StubGitService git = new StubGitService();
                IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);
                IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
                AdmiralService admiral = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);

                Vessel vessel = new Vessel("mode-vessel", "https://github.com/test/repo.git")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId
                };
                vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
                vessel.DefaultBranch = "main";
                vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                ServiceHarness harness = new ServiceHarness();
                harness._TestDb = testDb;
                harness.Logging = logging;
                harness.Settings = settings;
                harness.Admiral = admiral;
                harness.Objectives = new ObjectiveService(testDb.Driver, logging);
                harness.Vessel = vessel;
                return harness;
            }

            public VoyageDispatchService NewDispatchService()
            {
                return new VoyageDispatchService(_TestDb.Driver, Admiral, Logging, null, Objectives, Settings);
            }
        }
    }
}
