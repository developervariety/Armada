namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
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
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>
    /// Covers report-only dispatch: a Research objective must run its missions read-only however it is
    /// dispatched, so its Judge accepts a sound no-commit report instead of failing it for an empty
    /// diff. The autonomous scheduler already derived the mode from the objective Kind; the residual
    /// bug was that the operator dispatch path did not, so a Research objective linked to an
    /// <c>armada_dispatch</c> produced Implementation missions on an Implementation pipeline. These
    /// tests pin the single shared derivation rule, the operator path deriving from the linked
    /// objective, and complete read-only pipeline stage materialization.
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

            await RunTest("Operator dispatch appends the authoritative objective brief once", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ServiceHarness harness = await ServiceHarness.CreateAsync(testDb).ConfigureAwait(false);
                    Vessel referenceSource = await testDb.Driver.Vessels.CreateAsync(new Vessel("ReferenceSource", "https://example.test/reference-source.git")).ConfigureAwait(false);
                    harness.Vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                    {
                        new SiblingRepo { VesselRef = referenceSource.Id, RelativePath = "../ReferenceSource" }
                    });
                    await testDb.Driver.Vessels.UpdateAsync(harness.Vessel).ConfigureAwait(false);
                    Objective objective = await CreateObjectiveAsync(testDb, "Prepared operator dispatch", ObjectiveKindEnum.Feature).ConfigureAwait(false);
                    objective.Preparation = new ObjectivePreparation
                    {
                        RequiredSiblingInputs = new List<ObjectivePreparationSiblingInput>
                        {
                            new ObjectivePreparationSiblingInput { VesselRef = referenceSource.Name, RelativePath = "../ReferenceSource" }
                        },
                        Claims = new List<ObjectivePreparationClaim>
                        {
                            new ObjectivePreparationClaim
                            {
                                Kind = ObjectivePreparationClaimKindEnum.ReuseType,
                                Text = "Reuse ObjectiveBriefRenderer."
                            }
                        }
                    };
                    await testDb.Driver.Objectives.UpdateAsync(objective).ConfigureAwait(false);

                    VoyageDispatchService service = harness.NewDispatchService();
                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "prepared operator dispatch",
                        VesselId = harness.Vessel.Id,
                        CodeContextMode = "off",
                        ObjectiveId = objective.Id,
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("Build prepared change", "Keep this operator instruction.")
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);
                    AssertTrue(result.Succeeded, "prepared operator dispatch should succeed");

                    List<Mission> created = await testDb.Driver.Missions.EnumerateByVoyageAsync(result.Voyage!.Id).ConfigureAwait(false);
                    AssertEqual(1, created.Count, "one mission should be created");
                    AssertContains("Keep this operator instruction.", created[0].Description,
                        "The shared brief must preserve operator-specific instructions.");
                    AssertContains("Reuse ObjectiveBriefRenderer.", created[0].Description,
                        "The linked objective's prepared research must reach the operator mission.");
                    AssertContains("vessel `ReferenceSource` at `../ReferenceSource`", created[0].Description,
                        "Structured sibling preparation must reach the operator mission.");
                    AssertEqual(1, CountOccurrences(created[0].Description, "<!-- armada-objective-brief:"),
                        "The operator path must append one authoritative objective brief.");
                    AssertEqual("Keep this operator instruction.", request.Missions[0].Description,
                        "Objective enrichment must not mutate caller input used by a retry.");
                    AssertNull(request.Missions[0].StartFromRef,
                        "Objective defaults must not write inherited values into caller input.");
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

            await RunTest("Operator dispatch rejects an objective prepared for another vessel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ServiceHarness harness = await ServiceHarness.CreateAsync(testDb).ConfigureAwait(false);
                    Objective objective = await CreateObjectiveAsync(testDb, "Wrong target", ObjectiveKindEnum.Feature).ConfigureAwait(false);
                    objective.VesselIds = new List<string> { "vsl_other_target", "vsl_other_history" };
                    objective.Preparation = new ObjectivePreparation();
                    await testDb.Driver.Objectives.UpdateAsync(objective).ConfigureAwait(false);

                    VoyageDispatchResult result = await harness.NewDispatchService().DispatchAsync(new SharedVoyageDispatchRequest
                    {
                        Title = "mismatched dispatch",
                        VesselId = harness.Vessel.Id,
                        CodeContextMode = "off",
                        ObjectiveId = objective.Id,
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("Do not create", "This mission targets the wrong vessel.")
                        }
                    }).ConfigureAwait(false);

                    AssertFalse(result.Succeeded, "A mismatched objective target must fail before voyage creation.");
                    AssertContains("objective_vessel_mismatch", JsonSerializer.Serialize(result.Value));
                }
            });

            await RunTest("A Research mission on a Tested pipeline preserves every stage and dependency", async () =>
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
                    List<Mission> ordered = created.OrderBy(m => m.StageOrder).ToList();

                    AssertEqual(3, ordered.Count, "a read-only mission must preserve every declared pipeline stage");
                    AssertEqual("Worker", ordered[0].Persona, "the Worker stage must be first");
                    AssertEqual("TestEngineer", ordered[1].Persona, "the TestEngineer verification stage must be preserved");
                    AssertEqual("Judge", ordered[2].Persona, "the Judge review stage must be preserved");
                    AssertNull(ordered[0].DependsOnMissionId, "the first stage must have no dependency");
                    AssertEqual(ordered[0].Id, ordered[1].DependsOnMissionId, "the TestEngineer must depend on Worker");
                    AssertEqual(ordered[1].Id, ordered[2].DependsOnMissionId, "the Judge must depend on TestEngineer");
                    AssertTrue(created.All(m => m.IsReadOnlyMode),
                        "every materialized stage of a Research mission must be read-only");
                }
            });

            await RunTest("Autonomous and operator dispatch preserve the ReferencePortingTested graph", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ServiceHarness harness = await ServiceHarness.CreateAsync(testDb).ConfigureAwait(false);
                    Pipeline referencePipeline = new Pipeline("ReferencePortingTested");
                    referencePipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "PortingReferenceAnalyst"),
                        new PipelineStage(3, "TestEngineer"),
                        new PipelineStage(4, "Judge")
                    };
                    referencePipeline = await testDb.Driver.Pipelines.CreateAsync(referencePipeline).ConfigureAwait(false);

                    MissionModeEnum[] readOnlyModes = new[] { MissionModeEnum.Audit, MissionModeEnum.Research };
                    foreach (MissionModeEnum mode in readOnlyModes)
                    {
                        List<MissionDescription> autonomousDescriptions = new List<MissionDescription>
                        {
                            new MissionDescription(mode + " through the reference pipeline", "Inspect and report.")
                            {
                                Mode = mode.ToString()
                            }
                        };
                        Voyage autonomousVoyage = await harness.Admiral.DispatchVoyageAsync(
                            "autonomous " + mode + " reference voyage", "report only", harness.Vessel.Id,
                            autonomousDescriptions, referencePipeline.Id).ConfigureAwait(false);

                        VoyageDispatchResult operatorResult = await harness.NewDispatchService().DispatchAsync(
                            new SharedVoyageDispatchRequest
                            {
                                Title = "operator " + mode + " reference voyage",
                                VesselId = harness.Vessel.Id,
                                PipelineId = referencePipeline.Id,
                                CodeContextMode = "off",
                                Missions = new List<MissionDescription>
                                {
                                    new MissionDescription(mode + " through the reference pipeline", "Inspect and report.")
                                    {
                                        Mode = mode.ToString()
                                    }
                                }
                            }).ConfigureAwait(false);

                        AssertTrue(operatorResult.Succeeded, mode + " operator dispatch should succeed");
                        List<Mission> autonomousMissions = await testDb.Driver.Missions
                            .EnumerateByVoyageAsync(autonomousVoyage.Id).ConfigureAwait(false);
                        List<Mission> operatorMissions = await testDb.Driver.Missions
                            .EnumerateByVoyageAsync(operatorResult.Voyage!.Id).ConfigureAwait(false);

                        string autonomousGraph = String.Join(";", DescribePipelineGraph(autonomousMissions));
                        string operatorGraph = String.Join(";", DescribePipelineGraph(operatorMissions));
                        AssertEqual("Worker|1|none;PortingReferenceAnalyst|2|Worker;TestEngineer|3|PortingReferenceAnalyst;Judge|4|TestEngineer",
                            autonomousGraph, mode + " autonomous dispatch must persist the complete ordered graph");
                        AssertEqual(autonomousGraph, operatorGraph,
                            mode + " autonomous and operator dispatch must persist equivalent stage dependencies");
                        AssertTrue(autonomousMissions.All(m => m.IsReadOnlyMode),
                            mode + " must remain read-only across every autonomous stage");
                        AssertTrue(operatorMissions.All(m => m.IsReadOnlyMode),
                            mode + " must remain read-only across every operator stage");
                    }
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

        private static int CountOccurrences(string value, string search)
        {
            int count = 0;
            int offset = 0;
            while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += search.Length;
            }
            return count;
        }

        private static List<string> DescribePipelineGraph(List<Mission> missions)
        {
            List<Mission> ordered = missions.OrderBy(m => m.StageOrder).ToList();
            List<string> graph = new List<string>();
            foreach (Mission mission in ordered)
            {
                Mission? dependency = ordered.FirstOrDefault(candidate =>
                    String.Equals(candidate.Id, mission.DependsOnMissionId, StringComparison.Ordinal));
                graph.Add((mission.Persona ?? "") + "|" + mission.StageOrder + "|" + (dependency?.Persona ?? "none"));
            }
            return graph;
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
                settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 10;
                settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = 10;
                settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
                settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));

                StubGitService git = new StubGitService();
                IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(12345);
                IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
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
