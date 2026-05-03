namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Tests MissionService preferredModel routing for literal pins and tier selectors.
    /// </summary>
    public sealed class MissionServicePreferredModelRoutingTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MissionService PreferredModel Routing";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            string id = Guid.NewGuid().ToString("N");
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_model_route_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_model_route_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_model_route_logs_" + id);
            return settings;
        }

        private MissionService CreateMissionService(SqliteDatabaseDriver db, ArmadaSettings settings)
        {
            LoggingModule logging = CreateLogging();
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64001);
            return new MissionService(logging, db, settings, dockService, captainService);
        }

        private async Task<Vessel> CreateVesselAsync(SqliteDatabaseDriver db, ArmadaSettings settings)
        {
            Vessel vessel = new Vessel("model-routing-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/model-routing.git");
            vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
            vessel.DefaultBranch = "main";
            return await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private async Task<Captain> CreateCaptainAsync(
            SqliteDatabaseDriver db,
            string name,
            string model,
            string? allowedPersonas = null,
            string? preferredPersona = null,
            CaptainStateEnum state = CaptainStateEnum.Idle)
        {
            Captain captain = new Captain(name);
            captain.Model = model;
            captain.AllowedPersonas = allowedPersonas;
            captain.PreferredPersona = preferredPersona;
            captain.State = state;
            return await db.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private async Task<Mission> CreateMissionAsync(
            SqliteDatabaseDriver db,
            Vessel vessel,
            string title,
            string preferredModel,
            string? persona = null,
            string? preferredCaptainId = null)
        {
            Mission mission = new Mission(title, "Route by preferred model.");
            mission.VesselId = vessel.Id;
            mission.Status = MissionStatusEnum.Pending;
            mission.Persona = persona;
            mission.PreferredModel = preferredModel;
            mission.PreferredCaptainId = preferredCaptainId;
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("TryAssign_LiteralPreferredModel_AssignsExactModelCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "sonnet-captain", "claude-sonnet-4-6").ConfigureAwait(false);
                    Captain pinnedModel = await CreateCaptainAsync(testDb.Driver, "custom-captain", "custom-model-v1").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "literal route", "custom-model-v1").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Literal preferredModel should assign when an idle matching captain exists");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Mission should be launched");
                    AssertEqual(pinnedModel.Id, readBack.CaptainId, "Literal preferredModel should route to the exact model");
                }
            });

            await RunTest("TryAssign_TierPreferredModel_FiltersPersonaBeforeModelSelection", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "composer-worker", "composer-2-fast", "[\"Worker\"]").ConfigureAwait(false);
                    Captain judgeCaptain = await CreateCaptainAsync(testDb.Driver, "sonnet-judge", "claude-sonnet-4-6", "[\"Judge\"]").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "tier route", "mid", "Judge").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Tier preferredModel should choose a persona-eligible model");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Mission should be launched");
                    AssertEqual(judgeCaptain.Id, readBack.CaptainId, "Persona-ineligible mid model should not be selected");
                }
            });

            await RunTest("TryAssign_TierPreferredModel_PreservesPreferredPersonaWithinChosenModel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "sonnet-general", "claude-sonnet-4-6", "[\"Judge\"]").ConfigureAwait(false);
                    Captain preferredJudge = await CreateCaptainAsync(testDb.Driver, "sonnet-preferred", "claude-sonnet-4-6", "[\"Judge\"]", "Judge").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "tier preferred persona", "mid", "Judge").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Tier selected model should still use existing preferred-persona captain selection");
                    AssertEqual(preferredJudge.Id, readBack!.CaptainId, "PreferredPersona should win within the selected model");
                }
            });

            await RunTest("TryAssign_PinnedCaptainWithDisallowedTier_StaysPending", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Captain lowCaptain = await CreateCaptainAsync(testDb.Driver, "low-captain", "kimi-k2.5").ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "high-captain", "claude-opus-4-7").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "pinned tier mismatch", "high", null, lowCaptain.Id).ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Captain? lowReadBack = await testDb.Driver.Captains.ReadAsync(lowCaptain.Id).ConfigureAwait(false);
                    AssertFalse(assigned, "PreferredCaptainId should not fall back to a different captain when tier rejects the pin");
                    AssertEqual(MissionStatusEnum.Pending, readBack!.Status, "Mission should remain pending");
                    AssertNull(readBack.CaptainId, "No captain should be assigned");
                    AssertEqual(CaptainStateEnum.Idle, lowReadBack!.State, "Rejected pinned captain should stay idle");
                }
            });

            await RunTest("TryAssign_LiteralPreferredModelNoMatch_StaysPending", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "only-captain", "composer-2-fast").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "literal no match", "claude-opus-4-7").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertFalse(assigned, "Literal preferredModel with no matching idle captain should not assign");
                    AssertEqual(MissionStatusEnum.Pending, readBack!.Status, "Mission should remain pending cleanly");
                    AssertNull(readBack.CaptainId, "No captain should be assigned");
                }
            });
        }

    }
}
