namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Tests for the per-stage PreferredModel override on PipelineStage. The
    /// dispatcher should use stage.PreferredModel when set and fall back to the
    /// per-mission PreferredModel otherwise. Field must round-trip through the
    /// pipeline database driver (create -> read -> update).
    /// </summary>
    public class PerStagePreferredModelTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Per-Stage PreferredModel";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_test_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_test_repos_" + Guid.NewGuid().ToString("N"));
            return settings;
        }

        private async Task<Vessel> CreateVesselAsync(TestDatabase testDb, string name)
        {
            Vessel vessel = new Vessel(name, "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_test_work_" + Guid.NewGuid().ToString("N"));
            vessel.DefaultBranch = "main";
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private AdmiralService BuildAdmiral(TestDatabase testDb, LoggingModule logging, ArmadaSettings settings)
        {
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
            ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
            IMissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService);
            IVoyageService voyageService = new VoyageService(logging, testDb.Driver);
            return new AdmiralService(logging, testDb.Driver, settings, captainService, missionService, voyageService, dockService);
        }

        /// <summary>
        /// Run all tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("PipelineStage_PreferredModel_RoundTripsThroughDatabase", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Pipeline pipeline = new Pipeline("ReviewedTest_RoundTrip");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker") { PreferredModel = "claude-sonnet-4-6" },
                        new PipelineStage(2, "Judge") { PreferredModel = "claude-opus-4-7" }
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Pipeline? readBack = await testDb.Driver.Pipelines.ReadAsync(pipeline.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "Pipeline should round-trip");
                    AssertEqual(2, readBack!.Stages.Count, "Should have 2 stages");

                    PipelineStage? worker = readBack.Stages.FirstOrDefault(s => s.PersonaName == "Worker");
                    PipelineStage? judge = readBack.Stages.FirstOrDefault(s => s.PersonaName == "Judge");
                    AssertNotNull(worker, "Worker stage should round-trip");
                    AssertNotNull(judge, "Judge stage should round-trip");
                    AssertEqual("claude-sonnet-4-6", worker!.PreferredModel, "Worker stage PreferredModel should round-trip");
                    AssertEqual("claude-opus-4-7", judge!.PreferredModel, "Judge stage PreferredModel should round-trip");
                }
            });

            await RunTest("PipelineStage_PreferredModel_NullDefaults", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Pipeline pipeline = new Pipeline("NullModelPipeline");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker")
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Pipeline? readBack = await testDb.Driver.Pipelines.ReadAsync(pipeline.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "Pipeline should round-trip");
                    AssertEqual(1, readBack!.Stages.Count, "Should have 1 stage");
                    AssertNull(readBack.Stages[0].PreferredModel, "Unset PreferredModel should round-trip as null");
                }
            });

            await RunTest("PipelineStage_PreferredModel_UpdatePersists", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    Pipeline pipeline = new Pipeline("UpdatableModelPipeline");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    // Modify and update.
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker") { PreferredModel = "kimi-k2.5", PipelineId = pipeline.Id },
                        new PipelineStage(2, "Judge") { PreferredModel = "claude-opus-4-7", PipelineId = pipeline.Id }
                    };
                    await testDb.Driver.Pipelines.UpdateAsync(pipeline).ConfigureAwait(false);

                    Pipeline? readBack = await testDb.Driver.Pipelines.ReadAsync(pipeline.Id).ConfigureAwait(false);
                    AssertNotNull(readBack, "Pipeline should round-trip");
                    PipelineStage? worker = readBack!.Stages.FirstOrDefault(s => s.PersonaName == "Worker");
                    PipelineStage? judge = readBack.Stages.FirstOrDefault(s => s.PersonaName == "Judge");
                    AssertNotNull(worker, "Worker stage should exist after update");
                    AssertNotNull(judge, "Judge stage should exist after update");
                    AssertEqual("kimi-k2.5", worker!.PreferredModel, "Worker PreferredModel should update");
                    AssertEqual("claude-opus-4-7", judge!.PreferredModel, "Judge PreferredModel should update");
                }
            });

            await RunTest("Dispatch_StagePreferredModel_OverridesMissionPreferredModel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    AdmiralService admiral = BuildAdmiral(testDb, logging, settings);

                    Vessel vessel = await CreateVesselAsync(testDb, "perstage-override-vessel").ConfigureAwait(false);

                    Pipeline pipeline = new Pipeline("ReviewedOverride");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge") { PreferredModel = "claude-opus-4-7" }
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription("Feature work", "Implement feature with reviewed pipeline")
                        {
                            PreferredModel = "claude-sonnet-4-6"
                        }
                    };

                    Voyage voyage = await admiral.DispatchVoyageAsync(
                        "Override Voyage",
                        "Stage PreferredModel override test",
                        vessel.Id,
                        missions,
                        pipeline.Id).ConfigureAwait(false);
                    AssertNotNull(voyage, "Voyage should be created");

                    List<Mission> voyageMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(2, voyageMissions.Count, "Should have 2 missions for 2-stage pipeline");

                    Mission? worker = voyageMissions.FirstOrDefault(m => m.Persona == "Worker");
                    Mission? judge = voyageMissions.FirstOrDefault(m => m.Persona == "Judge");
                    AssertNotNull(worker, "Worker mission should exist");
                    AssertNotNull(judge, "Judge mission should exist");

                    // Worker stage has no PreferredModel override -> inherits the per-mission Sonnet pin.
                    AssertEqual("claude-sonnet-4-6", worker!.PreferredModel,
                        "Worker mission should inherit per-mission PreferredModel when stage override is null");
                    // Judge stage has PreferredModel="claude-opus-4-7" -> overrides per-mission Sonnet.
                    AssertEqual("claude-opus-4-7", judge!.PreferredModel,
                        "Judge mission should pick up stage-level PreferredModel override");
                }
            });

            await RunTest("Dispatch_StagePreferredModel_NullStage_FallsBackToMissionPin", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    AdmiralService admiral = BuildAdmiral(testDb, logging, settings);

                    Vessel vessel = await CreateVesselAsync(testDb, "perstage-null-vessel").ConfigureAwait(false);

                    // No stage-level PreferredModel anywhere - all stages should inherit the per-mission pin.
                    Pipeline pipeline = new Pipeline("ReviewedAllInherit");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    pipeline = await testDb.Driver.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    List<MissionDescription> missions = new List<MissionDescription>
                    {
                        new MissionDescription("Inherit test", "All stages inherit the per-mission pin")
                        {
                            PreferredModel = "claude-opus-4-7"
                        }
                    };

                    Voyage voyage = await admiral.DispatchVoyageAsync(
                        "Inherit Voyage",
                        "Stage PreferredModel inherit test",
                        vessel.Id,
                        missions,
                        pipeline.Id).ConfigureAwait(false);

                    List<Mission> voyageMissions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual(2, voyageMissions.Count, "Should have 2 missions for 2-stage pipeline");

                    foreach (Mission m in voyageMissions)
                    {
                        AssertEqual("claude-opus-4-7", m.PreferredModel,
                            "Stage with null PreferredModel should inherit per-mission pin (" + m.Persona + ")");
                    }
                }
            });
        }
    }
}
