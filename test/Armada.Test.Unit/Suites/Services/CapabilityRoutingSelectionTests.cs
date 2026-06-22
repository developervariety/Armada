namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Seam tests for the M3 capability-hint wiring. Where CapabilityRoutingTests exercise the
    /// PreferredModelTierSelector.SelectModel helper in isolation, these drive the REAL production
    /// assignment seam (MissionService.TryAssignAsync -> FindAvailableCaptainAsync) and the
    /// dispatch surface (VoyageDispatchService.DispatchAsync) so the hint is proven to flow from a
    /// dispatched MissionDescription, through Mission.CapabilityHint, into the within-tier captain
    /// selection that actually assigns a captain. The selection-only helper coverage cannot catch a
    /// regression where the hint is dropped before it reaches the selector or the dispatch path.
    /// </summary>
    public sealed class CapabilityRoutingSelectionTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Capability Routing Selection";

        // Concrete mid-tier models carrying the built-in default ModelCapabilityProfiles:
        //   sonnet     AuditReasoningFit=80 MechanicalThroughput=60
        //   gemini     AuditReasoningFit=60 MechanicalThroughput=60
        //   composer   AuditReasoningFit=30 MechanicalThroughput=80
        //   kimi       AuditReasoningFit=25 MechanicalThroughput=85
        // Default mid within-tier preference order is kimi, sonnet, composer.
        private const string _Sonnet = "claude-sonnet-4-6";
        private const string _Gemini = "gemini-3.5-pro";
        private const string _Composer = "composer-2.5";
        private const string _Kimi = "opencode-go/kimi-k2.7-code";
        private const string _Opus = "claude-opus-4-7";

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings()
        {
            string id = Guid.NewGuid().ToString("N");
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_caphint_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_caphint_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_caphint_logs_" + id);
            return settings;
        }

        private static MissionService CreateMissionService(SqliteDatabaseDriver db, ArmadaSettings settings, ICaptainQuarantineService quarantine)
        {
            LoggingModule logging = CreateLogging();
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64001);
            return new MissionService(logging, db, settings, dockService, captainService, captainQuarantine: quarantine);
        }

        private static async Task<Captain> SeedCaptainAsync(SqliteDatabaseDriver db, string name, string model, CaptainStateEnum state)
        {
            Captain captain = new Captain(name);
            captain.Model = model;
            captain.State = state;
            await db.Captains.CreateAsync(captain).ConfigureAwait(false);
            return captain;
        }

        private static async Task<Vessel> SeedVesselAsync(SqliteDatabaseDriver db, ArmadaSettings settings)
        {
            Vessel vessel = new Vessel("caphint-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/caphint.git");
            vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
            vessel.DefaultBranch = "main";
            await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);
            return vessel;
        }

        private static async Task<Mission> SeedMissionAsync(SqliteDatabaseDriver db, string vesselId, string? capabilityHint)
        {
            Mission mission = new Mission("Hinted mid mission", "Route by capability hint.");
            mission.VesselId = vesselId;
            mission.PreferredModel = "mid";
            mission.CapabilityHint = capabilityHint;
            mission.Status = MissionStatusEnum.Pending;
            await db.Missions.CreateAsync(mission).ConfigureAwait(false);
            return mission;
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("TryAssign_AuditHint_SelectsBestFitAuditCaptain_OverPreferenceOrder", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, CreateLogging());

                    Vessel vessel = await SeedVesselAsync(db, settings).ConfigureAwait(false);
                    // No-hint mid routing would pick kimi (first in the within-tier preference order).
                    // The audit hint maps to AuditReasoningFit, where sonnet (80) tops every idle peer,
                    // so a correct seam must override the preference-order default with sonnet.
                    Captain kimi = await SeedCaptainAsync(db, "cpt-kimi", _Kimi, CaptainStateEnum.Idle).ConfigureAwait(false);
                    Captain composer = await SeedCaptainAsync(db, "cpt-composer", _Composer, CaptainStateEnum.Idle).ConfigureAwait(false);
                    Captain sonnet = await SeedCaptainAsync(db, "cpt-sonnet", _Sonnet, CaptainStateEnum.Idle).ConfigureAwait(false);

                    Mission mission = await SeedMissionAsync(db, vessel.Id, "audit").ConfigureAwait(false);

                    MissionService missionService = CreateMissionService(db, settings, quarantine);
                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertTrue(assigned, "a hinted mission with eligible idle captains should assign");
                    Mission? updated = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(sonnet.Id, updated!.CaptainId, "audit hint must select the highest-AuditReasoningFit idle captain (sonnet)");
                    AssertNotEqual(kimi.Id, updated.CaptainId, "the preference-order default (kimi) must be overridden by the hint");
                    AssertNotEqual(composer.Id, updated.CaptainId, "a lower-audit idle captain must not win the audit hint");
                }
            });

            await RunTest("TryAssign_MechanicalHint_SelectsHighestThroughput_OverPreferenceOrder", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, CreateLogging());

                    Vessel vessel = await SeedVesselAsync(db, settings).ConfigureAwait(false);
                    // Idle set is sonnet, composer, gemini (no kimi). The mid preference order lists
                    // sonnet before composer, so a no-hint call would pick sonnet. The mechanical hint
                    // maps to MechanicalThroughput, where composer (80) beats sonnet (60) and gemini
                    // (60), proving a different dimension drives a different production assignment.
                    Captain sonnet = await SeedCaptainAsync(db, "cpt-sonnet", _Sonnet, CaptainStateEnum.Idle).ConfigureAwait(false);
                    Captain composer = await SeedCaptainAsync(db, "cpt-composer", _Composer, CaptainStateEnum.Idle).ConfigureAwait(false);
                    Captain gemini = await SeedCaptainAsync(db, "cpt-gemini", _Gemini, CaptainStateEnum.Idle).ConfigureAwait(false);

                    Mission mission = await SeedMissionAsync(db, vessel.Id, "mechanical").ConfigureAwait(false);

                    MissionService missionService = CreateMissionService(db, settings, quarantine);
                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertTrue(assigned, "a mechanical-hinted mission should assign");
                    Mission? updated = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(composer.Id, updated!.CaptainId, "mechanical hint must select the highest-MechanicalThroughput idle captain (composer)");
                    AssertNotEqual(sonnet.Id, updated.CaptainId, "the preference-order default (sonnet) must be overridden by the mechanical hint");
                    AssertNotEqual(gemini.Id, updated.CaptainId, "a lower-throughput idle captain must not win the mechanical hint");
                }
            });

            await RunTest("TryAssign_BestFitBusy_FallsToNextBestProfiledIdleCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, CreateLogging());

                    Vessel vessel = await SeedVesselAsync(db, settings).ConfigureAwait(false);
                    // sonnet is the top audit model but is Working (absent from the idle enumeration),
                    // so the next-best PROFILED idle model within the tier must win: gemini (60) over
                    // composer (30). This exercises the busy-best-fit fallback through the real seam.
                    Captain sonnetBusy = await SeedCaptainAsync(db, "cpt-sonnet", _Sonnet, CaptainStateEnum.Working).ConfigureAwait(false);
                    Captain composer = await SeedCaptainAsync(db, "cpt-composer", _Composer, CaptainStateEnum.Idle).ConfigureAwait(false);
                    Captain gemini = await SeedCaptainAsync(db, "cpt-gemini", _Gemini, CaptainStateEnum.Idle).ConfigureAwait(false);

                    Mission mission = await SeedMissionAsync(db, vessel.Id, "audit").ConfigureAwait(false);

                    MissionService missionService = CreateMissionService(db, settings, quarantine);
                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertTrue(assigned, "the mission should assign to the next-best idle captain");
                    Mission? updated = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(gemini.Id, updated!.CaptainId, "with the top audit model busy, the next-best profiled idle captain (gemini) wins");
                    AssertNotEqual(sonnetBusy.Id, updated.CaptainId, "a non-idle captain must never be assigned");
                    AssertNotEqual(composer.Id, updated.CaptainId, "a lower-audit idle captain must not jump ahead of the next-best");
                }
            });

            await RunTest("TryAssign_BestFitQuarantined_SkipsToNextBestProfiledIdleCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, CreateLogging());

                    Vessel vessel = await SeedVesselAsync(db, settings).ConfigureAwait(false);
                    // sonnet is the top audit model and is Idle, but quarantined (future retry deadline).
                    // The quarantine filter must remove it BEFORE the hint scores the survivors, so the
                    // audit hint falls to gemini (60) over composer (30): hint + quarantine composed.
                    Captain sonnetBenched = new Captain("cpt-sonnet");
                    sonnetBenched.Model = _Sonnet;
                    sonnetBenched.State = CaptainStateEnum.Idle;
                    sonnetBenched.QuarantineUntilUtc = DateTime.UtcNow.AddMinutes(30);
                    sonnetBenched.QuarantineReason = "You've hit your usage limit";
                    await db.Captains.CreateAsync(sonnetBenched).ConfigureAwait(false);

                    Captain composer = await SeedCaptainAsync(db, "cpt-composer", _Composer, CaptainStateEnum.Idle).ConfigureAwait(false);
                    Captain gemini = await SeedCaptainAsync(db, "cpt-gemini", _Gemini, CaptainStateEnum.Idle).ConfigureAwait(false);

                    Mission mission = await SeedMissionAsync(db, vessel.Id, "audit").ConfigureAwait(false);

                    MissionService missionService = CreateMissionService(db, settings, quarantine);
                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertTrue(assigned, "the mission should skip the benched best-fit and assign elsewhere");
                    Mission? updated = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(gemini.Id, updated!.CaptainId, "a quarantined best-fit captain is skipped; the next-best profiled idle captain (gemini) wins");
                    AssertNotEqual(sonnetBenched.Id, updated.CaptainId, "a quarantined captain on the best-fit model must never be selected");
                }
            });

            await RunTest("TryAssign_MidHint_NeverReachesIdleHighTierCaptain", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, CreateLogging());

                    Vessel vessel = await SeedVesselAsync(db, settings).ConfigureAwait(false);
                    // opus (high tier) has a higher AuditReasoningFit (95) than the idle mid captain
                    // gemini (60). If the hint leaked across tier boundaries it would reach into the
                    // reserved high tier and pick opus. A mid mission with a mid captain idle must stay
                    // in mid: the hint is a within-tier preference only, never a tier-jump.
                    Captain gemini = await SeedCaptainAsync(db, "cpt-gemini", _Gemini, CaptainStateEnum.Idle).ConfigureAwait(false);
                    Captain opus = await SeedCaptainAsync(db, "cpt-opus", _Opus, CaptainStateEnum.Idle).ConfigureAwait(false);

                    Mission mission = await SeedMissionAsync(db, vessel.Id, "audit").ConfigureAwait(false);

                    MissionService missionService = CreateMissionService(db, settings, quarantine);
                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertTrue(assigned, "a mid mission with an idle mid captain should assign within mid");
                    Mission? updated = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(gemini.Id, updated!.CaptainId, "a hinted mid mission stays within the mid tier (gemini)");
                    AssertNotEqual(opus.Id, updated.CaptainId, "the hint must not reach into the reserved high tier even when high scores higher");
                }
            });

            await RunTest("TryAssign_UnknownHint_DegradesToPreferenceOrderWithoutThrowing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, CreateLogging());

                    Vessel vessel = await SeedVesselAsync(db, settings).ConfigureAwait(false);
                    // An unrecognized hint must degrade to the no-hint within-tier preference result
                    // (kimi first) through the real seam, never throwing mid-assignment.
                    Captain kimi = await SeedCaptainAsync(db, "cpt-kimi", _Kimi, CaptainStateEnum.Idle).ConfigureAwait(false);
                    Captain sonnet = await SeedCaptainAsync(db, "cpt-sonnet", _Sonnet, CaptainStateEnum.Idle).ConfigureAwait(false);
                    Captain composer = await SeedCaptainAsync(db, "cpt-composer", _Composer, CaptainStateEnum.Idle).ConfigureAwait(false);

                    Mission mission = await SeedMissionAsync(db, vessel.Id, "totally-unknown-hint").ConfigureAwait(false);

                    MissionService missionService = CreateMissionService(db, settings, quarantine);
                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertTrue(assigned, "an unknown hint must not break assignment");
                    Mission? updated = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(kimi.Id, updated!.CaptainId, "an unknown hint degrades to the within-tier preference-order winner (kimi)");
                    AssertNotEqual(sonnet.Id, updated.CaptainId, "an unknown hint must not score by any dimension");
                    AssertNotEqual(composer.Id, updated.CaptainId, "an unknown hint must not score by any dimension");
                }
            });

            await RunTest("TryAssign_NullHint_MatchesNoHintPreferenceOrderResult", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    CaptainQuarantineService quarantine = new CaptainQuarantineService(db, settings, CreateLogging());

                    Vessel vessel = await SeedVesselAsync(db, settings).ConfigureAwait(false);
                    // A null hint is the backward-compatible path: selection follows the within-tier
                    // preference order exactly as it did before the hint was wired in (kimi first).
                    Captain kimi = await SeedCaptainAsync(db, "cpt-kimi", _Kimi, CaptainStateEnum.Idle).ConfigureAwait(false);
                    Captain sonnet = await SeedCaptainAsync(db, "cpt-sonnet", _Sonnet, CaptainStateEnum.Idle).ConfigureAwait(false);

                    Mission mission = await SeedMissionAsync(db, vessel.Id, null).ConfigureAwait(false);

                    MissionService missionService = CreateMissionService(db, settings, quarantine);
                    bool assigned = await missionService.TryAssignAsync(mission, vessel).ConfigureAwait(false);

                    AssertTrue(assigned, "a null-hint mission should assign by the preference order");
                    Mission? updated = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(kimi.Id, updated!.CaptainId, "a null hint follows the within-tier preference order (kimi first)");
                    AssertNotEqual(sonnet.Id, updated.CaptainId, "a null hint must not reorder by any capability dimension");
                }
            });

            await RunTest("Dispatch_MissionDescriptionWithCapabilityHint_PersistsHintOnMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // Round-trip the dispatch surface: a MissionDescription carrying CapabilityHint must
                    // produce a persisted Mission with that CapabilityHint. The alias marks the request
                    // so it routes through DispatchWithAliasesAsync -- the production seam the Worker
                    // wired -- rather than the legacy DispatchVoyageAsync path.
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("caphint-dispatch-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    PersistingAdmiralService admiral = new PersistingAdmiralService(testDb.Driver);
                    VoyageDispatchService service = new VoyageDispatchService(testDb.Driver, admiral, null, null, null, null);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "capability hint dispatch voyage",
                        VesselId = vessel.Id,
                        CodeContextMode = "off",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("hinted worker", "carry the hint")
                            {
                                Alias = "M1",
                                PreferredModel = "mid",
                                CapabilityHint = "audit"
                            }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertTrue(result.Succeeded, "dispatch carrying a capability hint should succeed");
                    List<Mission> missions = await testDb.Driver.Missions.EnumerateByVoyageAsync(result.Voyage!.Id).ConfigureAwait(false);
                    AssertEqual(1, missions.Count, "one mission should be created");
                    AssertEqual("audit", missions[0].CapabilityHint, "MissionDescription.CapabilityHint must flow onto the persisted mission");
                    AssertEqual("mid", missions[0].PreferredModel, "the existing preferredModel plumbing must remain intact alongside the hint");
                }
            });

            await RunTest("Dispatch_MissionDescriptionWithoutCapabilityHint_LeavesHintNull", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    // The hint is strictly opt-in: an omitted CapabilityHint must persist as null, never
                    // an empty string or a defaulted value, so default routing is unchanged.
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                        new Vessel("caphint-dispatch-null-vessel", "https://github.com/test/repo.git")
                        {
                            TenantId = Constants.DefaultTenantId,
                            UserId = Constants.DefaultUserId
                        }).ConfigureAwait(false);

                    PersistingAdmiralService admiral = new PersistingAdmiralService(testDb.Driver);
                    VoyageDispatchService service = new VoyageDispatchService(testDb.Driver, admiral, null, null, null, null);

                    SharedVoyageDispatchRequest request = new SharedVoyageDispatchRequest
                    {
                        Title = "no hint dispatch voyage",
                        VesselId = vessel.Id,
                        CodeContextMode = "off",
                        Missions = new List<MissionDescription>
                        {
                            new MissionDescription("plain worker", "no hint") { Alias = "M1" }
                        }
                    };

                    VoyageDispatchResult result = await service.DispatchAsync(request).ConfigureAwait(false);

                    AssertTrue(result.Succeeded, "dispatch without a hint should succeed");
                    List<Mission> missions = await testDb.Driver.Missions.EnumerateByVoyageAsync(result.Voyage!.Id).ConfigureAwait(false);
                    AssertEqual(1, missions.Count, "one mission should be created");
                    AssertNull(missions[0].CapabilityHint, "an omitted hint must persist as null");
                }
            });
        }

        /// <summary>
        /// Hand-rolled admiral double that persists each dispatched mission as-is and records it.
        /// Mirrors the create/read path used by the existing dispatch tests so the wired
        /// Mission.CapabilityHint can be read back; only the members the dispatch seam touches are
        /// implemented.
        /// </summary>
        private sealed class PersistingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            internal PersistingAdmiralService(DatabaseDriver database)
            {
                _Database = database ?? throw new ArgumentNullException(nameof(database));
            }

            internal List<Mission> CreatedMissions { get; } = new List<Mission>();

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public async Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
            {
                mission = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                CreatedMissions.Add(mission);
                return mission;
            }

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();
            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task RecallCaptainAsync(string captainId, CancellationToken token = default) => throw new NotImplementedException();
            public Task RecallAllAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HealthCheckAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task CleanupStaleCaptainsAsync(CancellationToken token = default) => throw new NotImplementedException();
            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => throw new NotImplementedException();
        }
    }
}
