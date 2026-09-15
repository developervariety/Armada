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
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;

    /// <summary>
    /// Tests that assignment honours a mission's requested captain and its stored fallback tier.
    /// </summary>
    public sealed class MissionServiceRequestedCaptainRoutingTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "MissionService Requested Captain Routing";

        private MissionService CreateMissionService(SqliteDatabaseDriver db, ArmadaSettings settings)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64101);
            return new MissionService(logging, db, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
        }

        private ArmadaSettings CreateSettings()
        {
            string id = Guid.NewGuid().ToString("N");
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_requested_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_requested_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_requested_logs_" + id);
            return settings;
        }

        private async Task<Vessel> CreateVesselAsync(SqliteDatabaseDriver db, ArmadaSettings settings)
        {
            Vessel vessel = new Vessel("requested-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/requested.git");
            vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
            vessel.DefaultBranch = "main";
            return await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private async Task<Captain> CreateCaptainAsync(
            SqliteDatabaseDriver db,
            string name,
            CaptainStateEnum state,
            CaptainTierEnum? tier,
            string? preferredPersona = null)
        {
            Captain captain = new Captain(name);
            captain.State = state;
            captain.Tier = tier;
            captain.AllowedPersonas = "[\"Worker\"]";
            captain.PreferredPersona = preferredPersona;
            return await db.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private async Task<Mission> CreateMissionAsync(SqliteDatabaseDriver db, Vessel vessel, string? requestedCaptainId, CaptainTierEnum? tier)
        {
            Mission mission = new Mission("requested captain routing", "Route by the requested captain.");
            mission.VesselId = vessel.Id;
            mission.Persona = "Worker";
            mission.Status = MissionStatusEnum.Pending;
            mission.RequestedCaptainId = requestedCaptainId;
            mission.Tier = tier;
            return await db.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private async Task<List<ArmadaEvent>> RequestedCaptainEventsAsync(SqliteDatabaseDriver db, string missionId)
        {
            List<ArmadaEvent> events = await db.Events.EnumerateByMissionAsync(missionId, 100).ConfigureAwait(false);
            return events.Where(evt => String.Equals(evt.EventType, RequestedCaptainAssignmentRule.EventType, StringComparison.Ordinal)).ToList();
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("TryAssign_IdleRequestedCaptain_IsChosenOverOtherIdleCaptains", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService service = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    // Normal routing would take the captain whose PreferredPersona matches.
                    await CreateCaptainAsync(testDb.Driver, "persona-match", CaptainStateEnum.Idle, CaptainTierEnum.Standard, "Worker").ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "other-idle", CaptainStateEnum.Idle, CaptainTierEnum.Standard).ConfigureAwait(false);
                    Captain requested = await CreateCaptainAsync(testDb.Driver, "requested", CaptainStateEnum.Idle, CaptainTierEnum.Standard).ConfigureAwait(false);

                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, requested.Id, null).ConfigureAwait(false);
                    AssertTrue(await service.TryAssignAsync(mission, vessel).ConfigureAwait(false), "An idle requested captain must be assigned");
                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(requested.Id, read!.CaptainId, "The requested captain must win over other idle captains");
                }
            });

            await RunTest("TryAssign_NoRequestedCaptainOrTier_KeepsNormalSelection", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService service = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "economy", CaptainStateEnum.Idle, CaptainTierEnum.Economy).ConfigureAwait(false);
                    Captain personaMatch = await CreateCaptainAsync(testDb.Driver, "persona-match", CaptainStateEnum.Idle, CaptainTierEnum.Premium, "Worker").ConfigureAwait(false);

                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, null, null).ConfigureAwait(false);
                    AssertTrue(await service.TryAssignAsync(mission, vessel).ConfigureAwait(false), "Normal routing must assign");
                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(personaMatch.Id, read!.CaptainId, "With neither field set the PreferredPersona captain is still chosen");
                    AssertEqual(0, (await RequestedCaptainEventsAsync(testDb.Driver, mission.Id).ConfigureAwait(false)).Count, "No requested-captain event without a request");
                }
            });

            await RunTest("TryAssign_BusyRequestedCaptainWithoutTierEligibleFallback_WaitsWithNamedReason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService service = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Captain requested = await CreateCaptainAsync(testDb.Driver, "busy-premium", CaptainStateEnum.Working, CaptainTierEnum.Premium).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "idle-economy", CaptainStateEnum.Idle, CaptainTierEnum.Economy).ConfigureAwait(false);

                    // No stored tier: the fallback floor is the requested captain's own tier.
                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, requested.Id, null).ConfigureAwait(false);
                    AssertFalse(await service.TryAssignAsync(mission, vessel).ConfigureAwait(false), "No captain at or above the requested captain's tier means no assignment");
                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNull(read!.CaptainId, "The mission must not be handed to a lower-tier substitute");
                    AssertEqual(MissionStatusEnum.Pending, read.Status, "The mission stays Pending");
                    AssertEqual(MissionAssignmentStateEnum.WaitingForIdleCaptain, read.AssignmentState, "The mission waits for a captain");

                    List<ArmadaEvent> events = await RequestedCaptainEventsAsync(testDb.Driver, mission.Id).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "The wait must be recorded once as a requested-captain event");
                    AssertContains(requested.Id, events[0].Message, "The event names the requested captain");
                    AssertContains("busy", events[0].Message, "The event names why the requested captain was not used");
                    AssertContains("Premium", events[0].Message, "The event names the fallback tier");

                    AssertFalse(await service.TryAssignAsync(mission, vessel).ConfigureAwait(false), "A repeated tick still waits");
                    AssertEqual(1, (await RequestedCaptainEventsAsync(testDb.Driver, mission.Id).ConfigureAwait(false)).Count, "An unchanged wait is not re-recorded every tick");
                }
            });

            await RunTest("TryAssign_QuarantinedRequestedCaptain_IsNeverUsedAndTheReasonIsNamed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService service = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Captain requested = new Captain("quarantined-requested");
                    requested.State = CaptainStateEnum.Idle;
                    requested.Tier = CaptainTierEnum.Premium;
                    requested.AllowedPersonas = "[\"Worker\"]";
                    requested.QuarantineUntilUtc = DateTime.UtcNow.AddHours(1);
                    requested = await testDb.Driver.Captains.CreateAsync(requested).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "idle-economy", CaptainStateEnum.Idle, CaptainTierEnum.Economy).ConfigureAwait(false);

                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, requested.Id, null).ConfigureAwait(false);
                    AssertFalse(await service.TryAssignAsync(mission, vessel).ConfigureAwait(false), "A quarantined requested captain must not be assigned");
                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertNull(read!.CaptainId, "Neither the quarantined captain nor a lower-tier substitute is assigned");

                    List<ArmadaEvent> events = await RequestedCaptainEventsAsync(testDb.Driver, mission.Id).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "The refusal must be recorded as a requested-captain event");
                    AssertContains("quarantined", events[0].Message, "The event names the quarantine");
                }
            });

            await RunTest("TryAssign_BusyRequestedCaptain_FallsBackToLowestTierAtOrAboveStoredTier", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService service = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Captain requested = await CreateCaptainAsync(testDb.Driver, "busy-requested", CaptainStateEnum.Working, CaptainTierEnum.Premium).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "idle-economy", CaptainStateEnum.Idle, CaptainTierEnum.Economy).ConfigureAwait(false);
                    await CreateCaptainAsync(testDb.Driver, "idle-premium", CaptainStateEnum.Idle, CaptainTierEnum.Premium).ConfigureAwait(false);
                    Captain standard = await CreateCaptainAsync(testDb.Driver, "idle-standard", CaptainStateEnum.Idle, CaptainTierEnum.Standard).ConfigureAwait(false);

                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, requested.Id, CaptainTierEnum.Standard).ConfigureAwait(false);
                    AssertTrue(await service.TryAssignAsync(mission, vessel).ConfigureAwait(false), "A tier-eligible idle captain takes the fallback");
                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(standard.Id, read!.CaptainId, "The fallback is the lowest idle tier at or above the stored Standard tier");

                    List<ArmadaEvent> events = await RequestedCaptainEventsAsync(testDb.Driver, mission.Id).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "The substitution must be recorded");
                    AssertContains(requested.Id, events[0].Message, "The fallback event names the requested captain");
                    AssertContains(standard.Id, events[0].Message, "The fallback event names the captain used instead");
                }
            });

            await RunTest("TryAssign_BusyRequestedCaptainWithoutStoredTier_FallsBackAtTheRequestedCaptainsTier", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    MissionService service = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);
                    Captain requested = await CreateCaptainAsync(testDb.Driver, "busy-premium", CaptainStateEnum.Working, CaptainTierEnum.Premium).ConfigureAwait(false);
                    // Normal routing would take the Standard captain whose PreferredPersona matches.
                    await CreateCaptainAsync(testDb.Driver, "idle-standard", CaptainStateEnum.Idle, CaptainTierEnum.Standard, "Worker").ConfigureAwait(false);
                    Captain premium = await CreateCaptainAsync(testDb.Driver, "idle-premium", CaptainStateEnum.Idle, CaptainTierEnum.Premium).ConfigureAwait(false);

                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, requested.Id, null).ConfigureAwait(false);
                    AssertTrue(await service.TryAssignAsync(mission, vessel).ConfigureAwait(false), "A Premium idle captain takes the fallback");
                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(premium.Id, read!.CaptainId, "Without a stored tier the fallback floor is the requested captain's tier");
                }
            });

            await RunTest("TryAssign_RequestedCaptainUnderUsageRouting_IsChosenFromUsageCandidates", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    Captain first = await CreateCaptainAsync(testDb.Driver, "usage-first", CaptainStateEnum.Idle, CaptainTierEnum.Standard).ConfigureAwait(false);
                    Captain requested = await CreateCaptainAsync(testDb.Driver, "usage-requested", CaptainStateEnum.Idle, CaptainTierEnum.Standard).ConfigureAwait(false);
                    settings.ModelTier.UsageRouting = new UsageRoutingSettings
                    {
                        Enabled = true,
                        Accounts = new List<UsageAccountSettings>
                        {
                            new UsageAccountSettings { Id = "first", CaptainIds = new List<string> { first.Id } },
                            new UsageAccountSettings { Id = "second", CaptainIds = new List<string> { requested.Id } }
                        },
                        PersonaRoutes = new Dictionary<string, List<UsageRouteSettings>>
                        {
                            ["Worker"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = "first" }, new UsageRouteSettings { AccountId = "second" } }
                        }
                    };
                    MissionService service = CreateMissionService(testDb.Driver, settings);
                    Vessel vessel = await CreateVesselAsync(testDb.Driver, settings).ConfigureAwait(false);

                    Mission mission = await CreateMissionAsync(testDb.Driver, vessel, requested.Id, null).ConfigureAwait(false);
                    AssertTrue(await service.TryAssignAsync(mission, vessel).ConfigureAwait(false), "Usage routing must assign");
                    Mission? read = await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(requested.Id, read!.CaptainId, "A requested captain that usage routing approves wins over the route order");
                }
            });
        }
    }
}
