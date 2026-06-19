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
            string? persona = null)
        {
            Mission mission = new Mission(title, "Route by preferred model.");
            mission.VesselId = vessel.Id;
            mission.Status = MissionStatusEnum.Pending;
            mission.Persona = persona;
            mission.PreferredModel = preferredModel;
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

            await RunTest("TryAssign_LiteralPreferredModelNoPin_AssignsPersonaEligibleCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "architect-pin", "gpt-5.5", "[\"Architect\"]").ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "opus-worker", "claude-opus-4-7", "[\"Worker\"]").ConfigureAwait(false);
                    Captain judgeCaptain = await CreateCaptainAsync(testDb.Driver, "opus-judge", "claude-opus-4-7", "[\"Judge\"]").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "literal persona route", "claude-opus-4-7", "Judge").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Literal preferredModel pool routing should succeed");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Mission should be launched");
                    AssertEqual(judgeCaptain.Id, readBack.CaptainId, "Literal preferredModel should route to a persona-eligible Judge captain");
                }
            });

            await RunTest("TryAssign_TierPreferredModel_FiltersPersonaBeforeModelSelection", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    // Judge is a specialist persona, so it resolves on high-tier captains only.
                    await CreateCaptainAsync(testDb.Driver, "opus-worker", "claude-opus-4-7", "[\"Worker\"]").ConfigureAwait(false);
                    Captain judgeCaptain = await CreateCaptainAsync(testDb.Driver, "gpt-judge", "gpt-5.5", "[\"Judge\"]").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "tier route", "high", "Judge").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Tier preferredModel should choose a persona-eligible model");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Mission should be launched");
                    AssertEqual(judgeCaptain.Id, readBack.CaptainId, "Persona-ineligible high model should not be selected");
                }
            });

            await RunTest("TryAssign_TierPreferredModel_PreservesPreferredPersonaWithinChosenModel", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    // Judge is a specialist persona, so it resolves on high-tier captains only.
                    await CreateCaptainAsync(testDb.Driver, "opus-general", "claude-opus-4-7", "[\"Judge\"]").ConfigureAwait(false);
                    Captain preferredJudge = await CreateCaptainAsync(testDb.Driver, "opus-preferred", "claude-opus-4-7", "[\"Judge\"]", "Judge").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "tier preferred persona", "high", "Judge").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Tier selected model should still use existing preferred-persona captain selection");
                    AssertEqual(preferredJudge.Id, readBack!.CaptainId, "PreferredPersona should win within the selected model");
                }
            });

            await RunTest("TryAssign_LiteralPreferredModel_CanonicalFamilyFallback_Assigns", async () =>
            {
                // claude-opus-4-8 is not in the curated high list but matches the canonical
                // opus pattern, so it classifies to high tier. When no exact captain is available,
                // the dispatch should fall back to any idle high-tier captain.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "mid-captain", "composer-2.5").ConfigureAwait(false);
                    Captain highCaptain = await CreateCaptainAsync(testDb.Driver, "high-captain", "claude-opus-4-7").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "canonical family fallback", "claude-opus-4-8").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Unavailable concrete model should fall back to a classified-tier captain");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Mission should be launched via tier fallback");
                    AssertEqual(highCaptain.Id, readBack.CaptainId, "High-tier fallback should route to the available high-tier captain");
                }
            });

            await RunTest("TryAssign_LiteralPreferredModel_UnclassifiedFallsBackToPersonaEligible_Assigns", async () =>
            {
                // When a pinned model name does not classify into any tier, fall back to any
                // idle captain compatible with the mission persona rather than blocking forever.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Captain anyCaptain = await CreateCaptainAsync(testDb.Driver, "generic-captain", "claude-opus-4-7", "[\"Worker\"]").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "unclassified fallback", "my-totally-unknown-model-v7", "Worker").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Unclassified concrete model should fall back to any persona-eligible captain");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Mission should be launched via persona fallback");
                    AssertEqual(anyCaptain.Id, readBack.CaptainId, "Unclassified fallback should route to the persona-eligible captain");
                }
            });

            await RunTest("TryAssign_LiteralPreferredModel_NoCompatibleTierCaptain_StaysPending", async () =>
            {
                // When the pinned model classifies to high tier but only mid-tier captains are
                // available, and the unclassified fallback path is not triggered, the mission
                // should remain pending rather than routing to an incompatible captain.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "mid-only", "composer-2.5").ConfigureAwait(false);
                    // claude-opus-4-8 classifies to high; only a mid-tier captain is available.
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "no high tier available", "claude-opus-4-8").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertFalse(assigned, "Classified high-tier pin with only mid-tier captains should not assign");
                    AssertEqual(MissionStatusEnum.Pending, readBack!.Status, "Mission should remain pending when tier fallback finds no eligible captain");
                    AssertNull(readBack.CaptainId, "No captain should be assigned");
                }
            });

            await RunTest("TryAssign_LiteralPreferredModel_ClassifiedTierUpgradesUpwardChain_Assigns", async () =>
            {
                // claude-sonnet-4-8 classifies to mid via the canonical sonnet pattern. With no
                // exact captain and no idle mid-tier captain, the classified-tier fallback delegates
                // to SelectModel, which follows the upward-only chain (mid -> high) and lands on the
                // available high-tier captain rather than leaving the mission pending.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Captain highCaptain = await CreateCaptainAsync(testDb.Driver, "high-only", "claude-opus-4-7").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "mid pin upward chain", "claude-sonnet-4-8").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Mid-classified concrete pin should follow the upward chain to a high-tier captain");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Mission should be launched via upward-chain fallback");
                    AssertEqual(highCaptain.Id, readBack.CaptainId, "Upward-chain fallback should route to the available high-tier captain");
                }
            });

            await RunTest("TryAssign_LiteralPreferredModel_ClassifiedTierPersonaIneligible_StaysPending", async () =>
            {
                // claude-opus-4-8 classifies to high. The only idle high-tier captain does not allow
                // the mission's Judge persona, so the classified-tier fallback (which filters by
                // persona inside SelectModel) must find no eligible model and leave the mission pending
                // rather than routing to a persona-incompatible captain.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "worker-only-high", "claude-opus-4-7", "[\"Worker\"]").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "classified persona ineligible", "claude-opus-4-8", "Judge").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertFalse(assigned, "Classified-tier fallback must not route to a persona-incompatible captain");
                    AssertEqual(MissionStatusEnum.Pending, readBack!.Status, "Mission should remain pending when no persona-eligible tier captain exists");
                    AssertNull(readBack.CaptainId, "No captain should be assigned");
                }
            });

            await RunTest("TryAssign_LiteralPreferredModel_UnclassifiedPersonaIneligible_StaysPending", async () =>
            {
                // An unclassified concrete pin drops the model filter and relies on the persona
                // filter below to narrow candidates. When the only idle captain cannot fill the
                // mission persona, the mission must stay pending instead of blindly assigning the
                // incompatible captain.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "worker-only", "claude-opus-4-7", "[\"Worker\"]").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "unclassified persona ineligible", "my-totally-unknown-model-v9", "Judge").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertFalse(assigned, "Unclassified fallback must still honor persona eligibility");
                    AssertEqual(MissionStatusEnum.Pending, readBack!.Status, "Mission should remain pending when the only captain cannot fill the persona");
                    AssertNull(readBack.CaptainId, "No captain should be assigned");
                }
            });

            await RunTest("TryAssign_NonSpecialistIdleMidAndHigh_AssignsMidNotHigh", async () =>
            {
                // End-to-end last-resort rule: with an idle mid AND an idle high captain, a
                // non-specialist mid mission must take the mid captain and leave high free.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Captain midCaptain = await CreateCaptainAsync(testDb.Driver, "mid-captain", "composer-2.5").ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "high-captain", "claude-opus-4-7").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "non-specialist mid route", "mid", "Worker").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Non-specialist mid mission should assign when an idle mid captain exists");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Mission should be launched");
                    AssertEqual(midCaptain.Id, readBack.CaptainId, "Non-specialist work must take the idle mid captain, not the high one");
                }
            });

            await RunTest("TryAssign_MidTier_K2_7FirstPreference_AssignsK2_7Captain", async () =>
            {
                // The default ModelTierSettings.WithinTierPreferenceOrder lists K2.7 first for
                // the mid tier. An idle K2.7 captain should win over other idle mid captains.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "sonnet-captain", "claude-sonnet-4-6").ConfigureAwait(false);
                    Captain k2Captain = await CreateCaptainAsync(testDb.Driver, "k2.7-captain", "opencode-go/kimi-k2.7-code").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "k2.7 preferred worker", "mid", "Worker").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Mid-tier Worker mission should assign when an idle K2.7 captain exists");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Mission should be launched");
                    AssertEqual(k2Captain.Id, readBack.CaptainId, "K2.7 captain should be chosen first for mid-tier Worker work");
                }
            });

            await RunTest("TryAssign_MidTier_K2_7Busy_FallsBackToSonnet", async () =>
            {
                // When no K2.7 captain is idle, the configured mid preference falls back to
                // sonnet before composer or other mid models.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Captain sonnetCaptain = await CreateCaptainAsync(testDb.Driver, "sonnet-captain", "claude-sonnet-4-6").ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "composer-captain", "composer-2.5").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "k2.7 busy fallback", "mid", "Worker").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Mid-tier Worker mission should fall back to sonnet when K2.7 is busy");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Mission should be launched");
                    AssertEqual(sonnetCaptain.Id, readBack.CaptainId, "Sonnet captain should be chosen as the K2.7 fallback");
                }
            });

            await RunTest("TryAssign_NonSpecialistOnlyHighIdle_FallsUpToHighLastResort", async () =>
            {
                // End-to-end last-resort fall-up: no idle mid/low captain exists, so a non-specialist
                // mid mission may use the idle high captain rather than stay pending.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Captain highCaptain = await CreateCaptainAsync(testDb.Driver, "high-only", "claude-opus-4-7").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "non-specialist last resort", "mid", "Worker").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Non-specialist mission should fall up to high when no mid/low captain is idle");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Mission should be launched via last-resort high");
                    AssertEqual(highCaptain.Id, readBack.CaptainId, "High is the last-resort captain when no mid/low captain is idle");
                }
            });

            await RunTest("TryAssign_NonSpecialistEmptyPreferredModel_DoesNotGrabIdleHigh", async () =>
            {
                // Empty-preferredModel leak fix: a non-specialist mission with no preferred model
                // must route through the unified selector at a mid default and NOT grab the idle
                // high captain while a mid captain sits free.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Captain midCaptain = await CreateCaptainAsync(testDb.Driver, "mid-captain", "composer-2.5").ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "high-captain", "claude-opus-4-7").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "empty preferred non-specialist", "", "Worker").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Empty preferredModel non-specialist mission should still assign");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Mission should be launched");
                    AssertEqual(midCaptain.Id, readBack.CaptainId, "Empty-preferredModel non-specialist must take the idle mid captain, not the high one");
                }
            });

            await RunTest("TryAssign_SpecialistEmptyPreferredModel_RoutesToHigh", async () =>
            {
                // Specialist default: a specialist mission with no preferred model defaults to high,
                // so it takes the idle high captain even though an idle mid captain exists.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService missions = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "mid-captain", "composer-2.5").ConfigureAwait(false);
                    Captain highCaptain = await CreateCaptainAsync(testDb.Driver, "high-captain", "claude-opus-4-7").ConfigureAwait(false);
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, "empty preferred specialist", "", "Judge").ConfigureAwait(false);

                    bool assigned = await missions.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    Mission? readBack = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertTrue(assigned, "Specialist mission with empty preferredModel should assign to a high captain");
                    AssertEqual(MissionStatusEnum.InProgress, readBack!.Status, "Mission should be launched");
                    AssertEqual(highCaptain.Id, readBack.CaptainId, "Specialist work defaults to the high captain even when a mid captain is idle");
                }
            });
        }

    }
}
