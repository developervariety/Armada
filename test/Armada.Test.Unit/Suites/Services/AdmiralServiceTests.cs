namespace Armada.Test.Unit.Suites.Services
{
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.Json;
    using Microsoft.Data.Sqlite;
    using Armada.Core;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using SyslogLogging;
    using FleetRoutingSettings = global::Test.Shared.Infrastructure.FleetRoutingSettings;

    public class AdmiralServiceTests : TestSuite
    {
        public override string Name => "Admiral Service";

        private static async Task SetMissionLastUpdateUtcAsync(TestDatabase testDb, string missionId, DateTime lastUpdateUtc)
        {
            using (SqliteConnection conn = new SqliteConnection(testDb.ConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE missions SET last_update_utc = @last_update_utc WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", missionId);
                    cmd.Parameters.AddWithValue("@last_update_utc", lastUpdateUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task ExecuteSqliteAsync(TestDatabase testDb, string sql)
        {
            using (SqliteConnection connection = new SqliteConnection(testDb.ConnectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        private static bool GetRetryDispatchNeeded(AdmiralService service)
        {
            System.Reflection.FieldInfo? field = typeof(AdmiralService).GetField(
                "_RetryDispatchNeeded",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null) throw new InvalidOperationException("Could not find _RetryDispatchNeeded field.");
            return (bool)field!.GetValue(service)!;
        }

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

        private AdmiralService CreateAdmiralService(LoggingModule logging, SqliteDatabaseDriver db, ArmadaSettings settings, StubGitService git)
        {
            IDockService dockService = new DockService(logging, db, settings, git);
            ICaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            IMissionService missionService = new MissionService(logging, db, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
            IVoyageService voyageService = new VoyageService(logging, db);
            return new AdmiralService(logging, db, settings, captainService, missionService, voyageService, dockService,
                git: git);
        }

        private static async Task<Mission> CreateExitMissionAsync(SqliteDatabaseDriver db, Captain captain, int processId)
        {
            Voyage voyage = new Voyage("Crash exclusion voyage " + processId) { Status = VoyageStatusEnum.InProgress };
            await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);
            Mission mission = new Mission("Crash exclusion mission " + processId)
            {
                VoyageId = voyage.Id,
                Status = MissionStatusEnum.InProgress,
                AssignmentState = MissionAssignmentStateEnum.Assigned,
                ProcessId = processId,
                StartedUtc = DateTime.UtcNow.AddSeconds(-5)
            };
            await db.Missions.CreateAsync(mission).ConfigureAwait(false);
            captain.CurrentMissionId = mission.Id;
            captain.ProcessId = processId;
            captain.State = CaptainStateEnum.Working;
            await db.Captains.UpdateAsync(captain).ConfigureAwait(false);
            return mission;
        }

        private sealed class RecordingEscalationService : IEscalationService
        {
            public int Evaluations { get; private set; }

            public Task EvaluateAsync(CancellationToken token = default)
            {
                Evaluations++;
                return Task.CompletedTask;
            }

            public Task FireAsync(EscalationTriggerEnum trigger, string entityId, string message, CancellationToken token = default)
            {
                return Task.CompletedTask;
            }
        }

        /// <summary>Quarantine service whose expired-quarantine restore throws on every call.</summary>
        private sealed class ThrowingRestoreQuarantineService : ICaptainQuarantineService
        {
            private readonly ICaptainQuarantineService _Inner;

            public ThrowingRestoreQuarantineService(ICaptainQuarantineService inner)
            {
                _Inner = inner;
            }

            public bool IsQuarantined(Captain captain) => _Inner.IsQuarantined(captain);
            public Task QuarantineAsync(Captain captain, string reason, DateTime? retryAfterUtc, CancellationToken token = default) => _Inner.QuarantineAsync(captain, reason, retryAfterUtc, token);
            public Task<bool> TryQuarantineCrashLoopAsync(string captainId, string reason, DateTime untilUtc, CancellationToken token = default) => _Inner.TryQuarantineCrashLoopAsync(captainId, reason, untilUtc, token);
            public Task<CaptainQuarantineResult> QuarantineCaptainAsync(AuthContext auth, string captainId, string? reason, DateTime? untilUtc, CancellationToken token = default) => _Inner.QuarantineCaptainAsync(auth, captainId, reason, untilUtc, token);
            public Task<CaptainQuarantineResult> ReleaseCaptainAsync(AuthContext auth, string captainId, CancellationToken token = default) => _Inner.ReleaseCaptainAsync(auth, captainId, token);
            public Task RestoreExpiredQuarantinesAsync(CancellationToken token = default) => throw new InvalidOperationException("unreadable quarantine row");
            public Task<bool> TryProbeRestoreAsync(Captain captain, CancellationToken token = default) => _Inner.TryProbeRestoreAsync(captain, token);
        }

        private sealed class AdmiralServiceHarborRecoveryHost : IHarborJobRecoveryHost
        {
            private readonly AdmiralService _Service;

            public AdmiralServiceHarborRecoveryHost(AdmiralService service)
            {
                _Service = service ?? throw new ArgumentNullException(nameof(service));
            }

            public Task<HarborJobRecoveryResult> RestoreAsync(HarborJobSnapshot job, CancellationToken token = default)
            {
                return Task.FromResult(new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Uncertain));
            }

            public Task<HarborJobRecoveryResult> TryAdoptAsync(HarborJobSnapshot job, HarborRunnerSession session, CancellationToken token = default)
            {
                return Task.FromResult(new HarborJobRecoveryResult(HarborJobRecoveryDispositionEnum.Uncertain));
            }

            public Task<bool> DeliverCompletionAsync(HarborJobSnapshot job, CancellationToken token = default)
            {
                return _Service.ResumeConfirmedCompletionAsync(job, token);
            }
        }

        private sealed class CrashLoopExcludedFailureCase
        {
            public CrashLoopExcludedFailureCase(string name, string logText, bool benchesCaptain)
            {
                Name = name;
                LogText = logText;
                BenchesCaptain = benchesCaptain;
            }

            public string Name { get; }

            public string LogText { get; }

            // Quota and credential failures bench the captain. A provider safeguard block does not: it follows
            // the refusal rule, which excludes the blocking runtime for that mission only.
            public bool BenchesCaptain { get; }
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Typed provider errors follow account policy and quota-first precedence", async () =>
            {
                foreach (int scenario in new[] { 0, 1, 2 })
                {
                    bool includeQuotaText = scenario == 1;
                    bool includeAuthText = scenario == 2;
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        SqliteDatabaseDriver db = testDb.Driver;
                        StubGitService git = new StubGitService();
                        ArmadaSettings settings = CreateSettings();
                        settings.MinIdleCaptains = 0;
                        settings.MaxRecoveryAttempts = 3;
                        settings.CaptainQuarantine.DefaultBackoffSeconds = 120;
                        settings.ModelTier.UsageRouting = new UsageRoutingSettings
                        {
                            Enabled = true,
                            Accounts = new List<UsageAccountSettings>
                            {
                                new UsageAccountSettings { Id = "shared", CaptainIds = new List<string>() },
                                new UsageAccountSettings { Id = "separate", CaptainIds = new List<string>() }
                            },
                            PersonaRoutes = new Dictionary<string, List<UsageRouteSettings>>
                            {
                                ["Worker"] = new List<UsageRouteSettings>
                                {
                                    new UsageRouteSettings { AccountId = "shared" },
                                    new UsageRouteSettings { AccountId = "separate" }
                                }
                            }
                        };
                        settings.ModelTier.ModelCapabilityProfiles["shared-model"] = new ModelCapabilityProfile
                        {
                            AuditReasoningFit = 83, MechanicalThroughput = 67, TelemetryRichness = 72, Cost = 31
                        };
                        string capabilityBefore = JsonSerializer.Serialize(settings.ModelTier.ModelCapabilityProfiles);
                        int processId = 5353 + scenario;
                        Captain failing = new Captain("typed-auth-first") { Model = "shared-model", State = CaptainStateEnum.Working, ProcessId = processId };
                        Captain sibling = new Captain("typed-auth-second") { Model = "shared-model", State = CaptainStateEnum.Idle };
                        Captain other = new Captain("typed-auth-other") { Model = "shared-model", State = CaptainStateEnum.Idle };
                        await db.Captains.CreateAsync(failing).ConfigureAwait(false);
                        await db.Captains.CreateAsync(sibling).ConfigureAwait(false);
                        await db.Captains.CreateAsync(other).ConfigureAwait(false);
                        settings.ModelTier.UsageRouting.Accounts[0].CaptainIds.Add(failing.Id);
                        settings.ModelTier.UsageRouting.Accounts[0].CaptainIds.Add(sibling.Id);
                        settings.ModelTier.UsageRouting.Accounts[1].CaptainIds.Add(other.Id);
                        AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);
                        Voyage voyage = new Voyage("Typed provider policy " + processId) { Status = VoyageStatusEnum.InProgress };
                        await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                        Mission mission = new Mission("Typed provider policy mission " + processId)
                        {
                            VoyageId = voyage.Id,
                            Persona = "Worker",
                            Status = MissionStatusEnum.InProgress,
                            AssignmentState = MissionAssignmentStateEnum.Assigned,
                            ProcessId = processId
                        };
                        await db.Missions.CreateAsync(mission).ConfigureAwait(false);
                        failing.CurrentMissionId = mission.Id;
                        await db.Captains.UpdateAsync(failing).ConfigureAwait(false);
                        if (includeQuotaText)
                        {
                            string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                            Directory.CreateDirectory(missionLogDir);
                            await File.WriteAllTextAsync(Path.Combine(missionLogDir, mission.Id + ".log"), "HTTP 429 rate limit reached\nAgent exited with code 1").ConfigureAwait(false);
                        }
                        else if (includeAuthText)
                        {
                            string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                            Directory.CreateDirectory(missionLogDir);
                            await File.WriteAllTextAsync(Path.Combine(missionLogDir, mission.Id + ".log"), "401 Unauthorized\nAgent exited with code 1").ConfigureAwait(false);
                        }

                        IAdmiralService admiral = service;
                        await admiral.HandleProcessExitAsync(processId, 1, failing.Id, mission.Id,
                            scenario == 2
                                ? new RuntimeProviderError { HttpStatusCode = 429 }
                                : new RuntimeProviderError { HttpStatusCode = 401 }, default).ConfigureAwait(false);

                        AssertEqual(capabilityBefore, JsonSerializer.Serialize(settings.ModelTier.ModelCapabilityProfiles),
                            "Provider auth/quota faults change availability, never configured model capability scores");
                        Mission? missionAfter = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                        AssertNotNull(missionAfter, "The typed provider failure keeps its mission stored.");
                        AssertEqual(MissionStatusEnum.Pending, missionAfter!.Status, "Typed provider evidence reroutes the failed mission.");
                        ProviderUsageStatus usageStatus = UsageRoutingService.For(settings).GetStatus(settings.ModelTier.UsageRouting.Accounts[0], null, DateTime.UtcNow);
                        if (scenario != 0)
                        {
                            AssertNotEqual(AccountLoginProbe.ReasonLoginRejected, usageStatus.Reason, "quota text outranks typed authentication and does not reject the login");
                            AssertEqual(UsageRoutingService.HoldKindQuota, usageStatus.HoldKind, "mixed quota/auth evidence follows the quota hold policy");
                        }
                        else
                        {
                            AssertEqual(AccountLoginProbe.ReasonLoginRejected, usageStatus.Reason, "typed 401 with neutral text rejects the usage-account login");
                            AssertEqual(UsageRoutingService.HoldKindAuth, usageStatus.HoldKind, "typed 401 uses the indefinite auth hold");
                            AssertNull(usageStatus.ExhaustedUntilUtc, "typed auth does not create a timed quota hold");
                        }
                        Captain? siblingAfter = await db.Captains.ReadAsync(sibling.Id).ConfigureAwait(false);
                        Captain? otherAfter = await db.Captains.ReadAsync(other.Id).ConfigureAwait(false);
                        UsageRoutingDecision decision = SmartRoutingTestSelect.Select(
                            UsageRoutingService.For(settings), settings.ModelTier.UsageRouting, missionAfter!,
                            new List<Captain> { siblingAfter!, otherAfter! }, Array.Empty<string>(), DateTime.UtcNow);
                        AssertEqual(1, decision.Candidates.Count, "the shared-account sibling is excluded and the separate account stays eligible");
                        AssertEqual(other.Id, decision.Candidates[0].Id, "the mission can reroute to the separate account");
                    }
                }
            });

            await RunTest("HandleProcessExitAsync records the process exit event in the mission owner's scope", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, CreateSettings(), new StubGitService());
                    string tenantId = Armada.Core.Constants.DefaultTenantId;
                    string userId = Armada.Core.Constants.DefaultUserId;

                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("exit-scope-vessel", "https://github.com/test/exit-scope.git")).ConfigureAwait(false);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("exit-scope-captain")).ConfigureAwait(false);
                    Mission mission = new Mission("exit scope mission");
                    mission.TenantId = tenantId;
                    mission.UserId = userId;
                    mission.VesselId = vessel.Id;
                    mission.CaptainId = captain.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
                    captain.CurrentMissionId = mission.Id;
                    captain.State = CaptainStateEnum.Working;
                    await testDb.Driver.Captains.UpdateAsync(captain).ConfigureAwait(false);

                    await service.HandleProcessExitAsync(4242, 0, captain.Id, mission.Id).ConfigureAwait(false);

                    EnumerationQuery query = new EnumerationQuery { MissionId = mission.Id, EventType = "captain.process_exited", PageNumber = 1, PageSize = 10 };
                    EnumerationResult<ArmadaEvent> owner = await testDb.Driver.Events.EnumerateAsync(tenantId, userId, query).ConfigureAwait(false);
                    AssertEqual(1, owner.Objects.Count, "The mission owner's scoped read must find the process exit event");
                    EnumerationResult<ArmadaEvent> other = await testDb.Driver.Events.EnumerateAsync(tenantId, "usr_other_owner", query).ConfigureAwait(false);
                    AssertEqual(0, other.Objects.Count, "Another user's scoped read must not find the process exit event");
                }
            });

            await RunTest("HarborCompletionIntent_ResumesAfterCaptainReleaseCrashWithoutRepeatingEffects", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    string tenantId = Armada.Core.Constants.DefaultTenantId;
                    string userId = Armada.Core.Constants.DefaultUserId;
                    string suffix = Guid.NewGuid().ToString("N");
                    string jobId = "hjob_completion_" + suffix;
                    string runnerId = "hbr_completion_" + suffix;
                    int processId = ProcessSupervisor.AllocateSyntheticProcessId();
                    DateTime processStartedUtc = DateTime.UtcNow;
                    ArmadaSettings settings = CreateSettings();
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_completion_intent_" + suffix, "logs");
                    LoggingModule logging = CreateLogging();
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(logging, db, settings, git);

                    Vessel vessel = await db.Vessels.CreateAsync(new Vessel("Completion intent " + suffix, "https://example.invalid/completion-intent")
                    {
                        TenantId = tenantId,
                        UserId = userId
                    }).ConfigureAwait(false);
                    Captain captain = new Captain("Completion intent " + suffix)
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        State = CaptainStateEnum.Working,
                        ProcessId = processId,
                        ProcessStartedUtc = processStartedUtc
                    };
                    Mission mission = new Mission("Completion intent " + suffix)
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        Status = MissionStatusEnum.InProgress,
                        AssignmentState = MissionAssignmentStateEnum.Assigned,
                        CaptainId = captain.Id,
                        VesselId = vessel.Id,
                        DockId = null,
                        HarborJobId = jobId,
                        ProcessId = processId,
                        ProcessStartedUtc = processStartedUtc,
                        StartedUtc = processStartedUtc
                    };
                    captain.CurrentMissionId = mission.Id;
                    Dock dock = new Dock(vessel.Id)
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        CaptainId = captain.Id,
                        WorktreePath = null,
                        Active = true
                    };
                    captain.CurrentDockId = dock.Id;
                    mission.DockId = dock.Id;

                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);
                    await db.Docks.CreateAsync(dock).ConfigureAwait(false);
                    await db.Missions.CreateAsync(mission).ConfigureAwait(false);
                    HarborJobRecord job = new HarborJobRecord
                    {
                        JobId = jobId,
                        RunnerId = runnerId,
                        LaunchKey = "mission:" + suffix,
                        TenantId = tenantId,
                        UserId = userId,
                        EnrollmentGeneration = 1,
                        SessionGeneration = 1,
                        State = HarborJobStateEnum.Failed,
                        ExitCode = 137,
                        FailureReason = "runner_process_exited",
                        MissionId = mission.Id,
                        CaptainId = captain.Id,
                        DockId = dock.Id,
                        AdmiralProcessId = processId,
                        AdmiralProcessStartedUtc = processStartedUtc,
                        StopConfirmed = true,
                        Revision = 3,
                        CompletedUtc = DateTime.UtcNow
                    };
                    await db.HarborJobs.CreateAsync(job).ConfigureAwait(false);

                    string triggerName = "fail_harbor_release_" + suffix;
                    await ExecuteSqliteAsync(testDb,
                        "CREATE TRIGGER " + triggerName + " BEFORE UPDATE OF process_id ON captains "
                        + "WHEN OLD.id = '" + captain.Id + "' AND OLD.process_id = " + processId + " AND NEW.process_id IS NULL "
                        + "BEGIN SELECT RAISE(FAIL, 'simulated Harbor completion crash'); END;").ConfigureAwait(false);

                    bool stoppedAtCaptainRelease = false;
                    try
                    {
                        await service.HandleProcessExitAsync(processId, 137, captain.Id, mission.Id).ConfigureAwait(false);
                    }
                    catch (SqliteException exception)
                    {
                        stoppedAtCaptainRelease = exception.Message.Contains("simulated Harbor completion crash", StringComparison.Ordinal);
                    }
                    AssertTrue(stoppedAtCaptainRelease, "the real process-exit handler writes the pending outcome before the captain release fails");

                    HarborCompletionIntent? pendingIntent = await db.HarborJobs.ReadCompletionIntentAsync(jobId).ConfigureAwait(false);
                    AssertNotNull(pendingIntent, "the computed completion intent survives the interrupted exit handler");
                    AssertFalse(pendingIntent!.Completed, "the incomplete captain release does not acknowledge the intent");
                    Mission? pendingMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, pendingMission!.Status, "mission reset was persisted before the simulated crash");
                    AssertEqual(jobId, pendingMission.HarborJobId, "the pending mission keeps its completed Harbor job identity");
                    AssertNull(pendingMission.CaptainId, "the pending mission no longer names the old captain");
                    Captain? stillBound = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(mission.Id, stillBound!.CurrentMissionId, "failed captain CAS preserves the old pointer for recovery");
                    AssertEqual(processId, stillBound.ProcessId ?? 0, "failed captain CAS preserves the old process identity");

                    await ExecuteSqliteAsync(testDb, "DROP TRIGGER " + triggerName + ";").ConfigureAwait(false);
                    AdmiralService restartedService = CreateAdmiralService(CreateLogging(), db, settings, git);
                    HarborRunnerEnrollmentService enrollments = new HarborRunnerEnrollmentService(db);
                    HarborRunnerSessionRegistry registry = new HarborRunnerSessionRegistry(true, enrollments);
                    HarborJobCoordinator coordinator = new HarborJobCoordinator(registry, enrollments, db.HarborJobs);
                    AssertTrue(await coordinator.InitializeRecoveryAsync(new AdmiralServiceHarborRecoveryHost(restartedService)).ConfigureAwait(false),
                        "restart recovery resumes the persisted completion intent before acknowledging the job");

                    HarborJobRecord? recoveredJob = await db.HarborJobs.ReadAsync(jobId).ConfigureAwait(false);
                    AssertTrue(recoveredJob!.CompletionDelivered, "the exact lifecycle continuation is acknowledged after it completes");
                    HarborCompletionIntent? completedIntent = await db.HarborJobs.ReadCompletionIntentAsync(jobId).ConfigureAwait(false);
                    AssertTrue(completedIntent!.Completed, "the resumed intent completes after mission, captain, and audit effects");
                    Mission? recoveredMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, recoveredMission!.Status, "restart leaves the same mission queued once");
                    AssertEqual(jobId, recoveredMission.HarborJobId, "restart preserves the completion owner on the queued mission");
                    Captain? recoveredCaptain = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Stalled, recoveredCaptain!.State, "the stored captain policy is applied");
                    AssertNull(recoveredCaptain.CurrentMissionId, "the old captain is released");
                    AssertNull(recoveredCaptain.ProcessId, "the old synthetic process identity is cleared");
                    Dock? recoveredDock = await db.Docks.ReadAsync(tenantId, dock.Id).ConfigureAwait(false);
                    AssertFalse(recoveredDock!.Active, "the old dock is reclaimed before completion acknowledgment");

                    MissionAttemptFact? fact = await db.MissionAttemptFacts.ReadAsync(completedIntent.FactId).ConfigureAwait(false);
                    ArmadaEvent? retryEvent = await db.Events.ReadAsync(completedIntent.EventId).ConfigureAwait(false);
                    Signal? signal = await db.Signals.ReadAsync(completedIntent.SignalId).ConfigureAwait(false);
                    AssertNotNull(fact, "the stable retry fact is restored");
                    AssertNotNull(retryEvent, "the stable retry event is restored");
                    AssertNotNull(signal, "the stable captain signal is restored");

                    HarborJobSnapshot? recoveredSnapshot = null;
                    foreach (HarborJobSnapshot snapshot in coordinator.GetSnapshots())
                    {
                        if (String.Equals(snapshot.JobId, jobId, StringComparison.Ordinal)) recoveredSnapshot = snapshot;
                    }
                    AssertNotNull(recoveredSnapshot, "the coordinator retains the completed job snapshot");
                    AssertTrue(await restartedService.ResumeConfirmedCompletionAsync(recoveredSnapshot!).ConfigureAwait(false),
                        "replaying the completed intent is safe");
                    AssertEqual(completedIntent.FactId, (await db.MissionAttemptFacts.ReadAsync(completedIntent.FactId).ConfigureAwait(false))!.Id,
                        "replay keeps one append-only retry fact");
                    AssertEqual(completedIntent.EventId, (await db.Events.ReadAsync(completedIntent.EventId).ConfigureAwait(false))!.Id,
                        "replay keeps one lifecycle event");
                    AssertEqual(completedIntent.SignalId, (await db.Signals.ReadAsync(completedIntent.SignalId).ConfigureAwait(false))!.Id,
                        "replay keeps one signal");
                    AssertEqual(1, (await db.Events.EnumerateByTypeAsync("mission.retry_requeued", 20).ConfigureAwait(false)).Count,
                        "recovery does not emit a second retry event or consume another retry slot");
                }
            });

            // A mission awaiting review keeps its dock and releases its captain; approval lands from that
            // dock. The orphan reaper must not take it, while a dock no mission needs is still reclaimed.
            await RunTest("The orphan-dock reaper keeps the dock of a mission awaiting review and reclaims a real orphan", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, CreateSettings(), new StubGitService());
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("reaper-vessel", "https://github.com/test/reaper.git")).ConfigureAwait(false);
                    Captain idle = await testDb.Driver.Captains.CreateAsync(new Captain("reaper-idle-captain") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);

                    Dock reviewDock = await testDb.Driver.Docks.CreateAsync(new Dock(vessel.Id)
                    {
                        CaptainId = idle.Id, Active = true, WorktreePath = Path.Combine(Path.GetTempPath(), "armada_reaper_review_" + Guid.NewGuid().ToString("N")),
                        BranchName = "armada/reaper/review", CreatedUtc = DateTime.UtcNow.AddMinutes(-30)
                    }).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(new Mission("Awaiting review") { VesselId = vessel.Id, DockId = reviewDock.Id, Status = MissionStatusEnum.Review }).ConfigureAwait(false);
                    Dock orphanDock = await testDb.Driver.Docks.CreateAsync(new Dock(vessel.Id)
                    {
                        CaptainId = idle.Id, Active = true, WorktreePath = Path.Combine(Path.GetTempPath(), "armada_reaper_orphan_" + Guid.NewGuid().ToString("N")),
                        BranchName = "armada/reaper/orphan", CreatedUtc = DateTime.UtcNow.AddMinutes(-30)
                    }).ConfigureAwait(false);

                    await service.ReclaimOrphanedDocksAsync().ConfigureAwait(false);

                    AssertTrue((await testDb.Driver.Docks.ReadAsync(reviewDock.Id).ConfigureAwait(false))!.Active, "The dock of a mission awaiting review is kept");
                    AssertFalse((await testDb.Driver.Docks.ReadAsync(orphanDock.Id).ConfigureAwait(false))!.Active, "A dock no mission needs is reclaimed");
                }
            });

            // A restart-preserved root mission continues on its committed branch from a retained active dock.
            // A failed provision clears the mission's DockId pointer while keeping the committed branch, so the
            // reaper must still preserve the dock by the branch identity the assignment path authorizes
            // continuation with (tenant/vessel/branch); otherwise it reclaims the authority dock and the mission
            // later strands on root_continuation_dock_missing. A dock whose branch no root mission claims is
            // still reclaimed.
            await RunTest("The orphan-dock reaper keeps a retained dock a pending root mission still claims by branch identity after its DockId pointer was cleared", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, CreateSettings(), new StubGitService());
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("reaper-identity-vessel", "https://github.com/test/reaper-identity.git")).ConfigureAwait(false);
                    Captain idle = await testDb.Driver.Captains.CreateAsync(new Captain("reaper-identity-captain") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);

                    string committedBranch = "armada/captain-a/root-continuation";
                    Dock retained = await testDb.Driver.Docks.CreateAsync(new Dock(vessel.Id)
                    {
                        TenantId = vessel.TenantId, CaptainId = idle.Id, Active = true,
                        WorktreePath = Path.Combine(Path.GetTempPath(), "armada_reaper_retained_" + Guid.NewGuid().ToString("N")),
                        BranchName = committedBranch, CreatedUtc = DateTime.UtcNow.AddMinutes(-30)
                    }).ConfigureAwait(false);

                    // The failed provision cleared the DockId pointer but kept the committed branch.
                    await testDb.Driver.Missions.CreateAsync(new Mission("Retained root")
                    {
                        VesselId = vessel.Id, TenantId = vessel.TenantId, BranchName = committedBranch,
                        DockId = null, DependsOnMissionId = null, Status = MissionStatusEnum.Pending
                    }).ConfigureAwait(false);

                    // A genuine orphan: an active dock whose branch no pending root mission claims.
                    Dock orphanDock = await testDb.Driver.Docks.CreateAsync(new Dock(vessel.Id)
                    {
                        TenantId = vessel.TenantId, CaptainId = idle.Id, Active = true,
                        WorktreePath = Path.Combine(Path.GetTempPath(), "armada_reaper_identity_orphan_" + Guid.NewGuid().ToString("N")),
                        BranchName = "armada/reaper/identity-orphan", CreatedUtc = DateTime.UtcNow.AddMinutes(-30)
                    }).ConfigureAwait(false);

                    await service.ReclaimOrphanedDocksAsync().ConfigureAwait(false);

                    AssertTrue((await testDb.Driver.Docks.ReadAsync(retained.Id).ConfigureAwait(false))!.Active,
                        "A retained dock a pending root mission still claims by branch identity is kept");
                    AssertFalse((await testDb.Driver.Docks.ReadAsync(orphanDock.Id).ConfigureAwait(false))!.Active,
                        "A dock no root mission claims is still reclaimed");
                }
            });

            // A restart kills a running or queued gate before the self-heal holds the mission for it, and the gate
            // re-run tests the mission's dock. The reaper keeps that dock until the gate records a result, and
            // reclaims the dock of a produced mission whose gate already passed.
            await RunTest("The orphan-dock reaper keeps the dock of a produced mission whose gate has no result", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, testDb.Driver, settings, git);
                    ICaptainService captainService = new CaptainService(logging, testDb.Driver, settings, git, dockService);
                    MissionService missionService = new MissionService(logging, testDb.Driver, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    missionService.DefinitionOfDone = new DefinitionOfDoneGate(
                        new DefinitionOfDoneSettings { Enabled = true, RunRestoreBeforeBuild = false }, testDb.Driver, logging);
                    AdmiralService service = new AdmiralService(logging, testDb.Driver, settings, captainService, missionService,
                        new VoyageService(logging, testDb.Driver), dockService, git: git);

                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("reaper-gate-vessel", "https://github.com/test/reaper-gate.git")).ConfigureAwait(false);
                    Captain idle = await testDb.Driver.Captains.CreateAsync(new Captain("reaper-gate-captain") { State = CaptainStateEnum.Idle }).ConfigureAwait(false);

                    Dock ungatedDock = await testDb.Driver.Docks.CreateAsync(new Dock(vessel.Id)
                    {
                        CaptainId = idle.Id, Active = true, WorktreePath = Path.Combine(Path.GetTempPath(), "armada_reaper_ungated_" + Guid.NewGuid().ToString("N")),
                        BranchName = "armada/reaper/ungated", CreatedUtc = DateTime.UtcNow.AddMinutes(-30)
                    }).ConfigureAwait(false);
                    await testDb.Driver.Missions.CreateAsync(new Mission("Gate interrupted")
                    {
                        VesselId = vessel.Id, DockId = ungatedDock.Id, Status = MissionStatusEnum.WorkProduced, StartedUtc = DateTime.UtcNow.AddMinutes(-20)
                    }).ConfigureAwait(false);

                    Dock gatedDock = await testDb.Driver.Docks.CreateAsync(new Dock(vessel.Id)
                    {
                        CaptainId = idle.Id, Active = true, WorktreePath = Path.Combine(Path.GetTempPath(), "armada_reaper_gated_" + Guid.NewGuid().ToString("N")),
                        BranchName = "armada/reaper/gated", CreatedUtc = DateTime.UtcNow.AddMinutes(-30)
                    }).ConfigureAwait(false);
                    Mission gated = await testDb.Driver.Missions.CreateAsync(new Mission("Gate passed")
                    {
                        VesselId = vessel.Id, DockId = gatedDock.Id, Status = MissionStatusEnum.WorkProduced, StartedUtc = DateTime.UtcNow.AddMinutes(-20)
                    }).ConfigureAwait(false);
                    DefinitionOfDoneEvaluationRecord record = DefinitionOfDoneEvaluationRecord.FromResult(DefinitionOfDoneResult.Pass(), DateTime.UtcNow.AddMinutes(-2));
                    ArmadaEvent evt = new ArmadaEvent(DefinitionOfDoneEvaluationRecord.EventType, "Definition-of-done evaluation: " + record.Outcome);
                    evt.EntityType = "mission";
                    evt.EntityId = gated.Id;
                    evt.MissionId = gated.Id;
                    evt.VesselId = vessel.Id;
                    evt.Payload = System.Text.Json.JsonSerializer.Serialize(record);
                    evt.CreatedUtc = DateTime.UtcNow.AddMinutes(-1);
                    await testDb.Driver.Events.CreateAsync(evt).ConfigureAwait(false);

                    await service.ReclaimOrphanedDocksAsync().ConfigureAwait(false);

                    AssertTrue((await testDb.Driver.Docks.ReadAsync(ungatedDock.Id).ConfigureAwait(false))!.Active,
                        "The dock of a produced mission whose gate has no result is kept for the gate re-run");
                    AssertFalse((await testDb.Driver.Docks.ReadAsync(gatedDock.Id).ConfigureAwait(false))!.Active,
                        "The dock of a produced mission whose gate passed is reclaimed");
                }
            });

            await RunTest("A captain Stalled with no mission returns to Idle after the stall threshold, and one with a mission stays", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    settings.StallThresholdMinutes = 10;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, settings, new StubGitService());

                    Captain orphaned = await testDb.Driver.Captains.CreateAsync(new Captain("stalled-no-mission")).ConfigureAwait(false);
                    await testDb.Driver.Captains.UpdateStateAsync(orphaned.Id, CaptainStateEnum.Stalled).ConfigureAwait(false);
                    Captain holding = await testDb.Driver.Captains.CreateAsync(new Captain("stalled-with-mission")).ConfigureAwait(false);
                    holding.CurrentMissionId = "msn_still_owned";
                    holding.State = CaptainStateEnum.Stalled;
                    await testDb.Driver.Captains.UpdateAsync(holding).ConfigureAwait(false);

                    int early = await service.RecoverStalledCaptainsAsync(CancellationToken.None, DateTime.UtcNow.AddMinutes(2)).ConfigureAwait(false);
                    AssertEqual(0, early, "a captain stalled for less than the threshold is left alone");
                    AssertEqual(CaptainStateEnum.Stalled, (await testDb.Driver.Captains.ReadAsync(orphaned.Id).ConfigureAwait(false))!.State);

                    int recovered = await service.RecoverStalledCaptainsAsync(CancellationToken.None, DateTime.UtcNow.AddMinutes(11)).ConfigureAwait(false);
                    AssertEqual(1, recovered, "only the captain with no mission is recovered");
                    AssertEqual(CaptainStateEnum.Idle, (await testDb.Driver.Captains.ReadAsync(orphaned.Id).ConfigureAwait(false))!.State, "the orphaned captain returns to Idle");
                    AssertEqual(CaptainStateEnum.Stalled, (await testDb.Driver.Captains.ReadAsync(holding.Id).ConfigureAwait(false))!.State, "a captain that still owns a mission stays Stalled");

                    List<ArmadaEvent> events = await testDb.Driver.Events.EnumerateByTypeAsync("captain.stall_recovered", 10).ConfigureAwait(false);
                    AssertEqual(1, events.Count, "the recovery is recorded");
                    AssertEqual(orphaned.Id, events[0].EntityId);
                }
            });

            await RunTest("ArmadaSettings MaxInterruptedExitRedispatchAttempts ClampsToRangeAndDefaultsToTwo", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                AssertEqual(2, settings.MaxInterruptedExitRedispatchAttempts, "the default budget is two re-dispatches");

                settings.MaxInterruptedExitRedispatchAttempts = -3;
                AssertEqual(0, settings.MaxInterruptedExitRedispatchAttempts, "the budget clamps to the minimum");

                settings.MaxInterruptedExitRedispatchAttempts = 99;
                AssertEqual(10, settings.MaxInterruptedExitRedispatchAttempts, "the budget clamps to the maximum");

                settings.MaxInterruptedExitRedispatchAttempts = 4;
                AssertEqual(4, settings.MaxInterruptedExitRedispatchAttempts, "an in-range budget is kept");
            });

            await RunTest("ArmadaSettings LaunchProcessIdGraceSeconds ClampsToRange", () =>
            {
                ArmadaSettings settings = CreateSettings();

                settings.LaunchProcessIdGraceSeconds = 0;
                AssertEqual(5, settings.LaunchProcessIdGraceSeconds, "Launch PID grace should clamp to the minimum");

                settings.LaunchProcessIdGraceSeconds = 999;
                AssertEqual(300, settings.LaunchProcessIdGraceSeconds, "Launch PID grace should clamp to the maximum");

                settings.LaunchProcessIdGraceSeconds = 45;
                AssertEqual(45, settings.LaunchProcessIdGraceSeconds, "Launch PID grace should preserve in-range values");
            });

            await RunTest("SpendCapBench BenchesOnlyTheCappedModel", async () =>
            {
                // A provider spend cap is scoped to the model, not the provider account: an expensive frontier
                // model can be capped while the cheap models on the SAME provider still answer. Benching the whole
                // "example-provider/" prefix took working captains offline, so only the capped model may be benched here.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Captain capped = new Captain("capped-1");
                    capped.Model = "example-provider/gpt-5.6-luna";
                    Captain sameModel = new Captain("capped-2");
                    sameModel.Model = "example-provider/gpt-5.6-luna";
                    Captain sameProviderOtherModel = new Captain("cheap-1");
                    sameProviderOtherModel.Model = "example-provider/gpt-5.6-sol";
                    Captain otherProvider = new Captain("native-1");
                    otherProvider.Model = "claude-opus-5";

                    await db.Captains.CreateAsync(capped);
                    await db.Captains.CreateAsync(sameModel);
                    await db.Captains.CreateAsync(sameProviderOtherModel);
                    await db.Captains.CreateAsync(otherProvider);

                    System.Reflection.MethodInfo? bench = typeof(AdmiralService).GetMethod(
                        "BenchProviderGroupAsync",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (bench == null) throw new InvalidOperationException("Could not find BenchProviderGroupAsync.");

                    DateTime until = DateTime.UtcNow.AddHours(25);
                    await (Task)bench.Invoke(service, new object[]
                    {
                        capped, "Provider daily spend cap reached.", until, CancellationToken.None
                    })!;

                    Captain? benched1 = await db.Captains.ReadAsync(capped.Id);
                    Captain? benched2 = await db.Captains.ReadAsync(sameModel.Id);
                    Captain? cheap = await db.Captains.ReadAsync(sameProviderOtherModel.Id);
                    Captain? native = await db.Captains.ReadAsync(otherProvider.Id);

                    AssertTrue(benched1!.QuarantineUntilUtc.HasValue,
                        "The captain that returned the cap must be benched");
                    AssertTrue(benched2!.QuarantineUntilUtc.HasValue,
                        "A second captain on the SAME capped model must be benched in the same pass");
                    AssertFalse(cheap!.QuarantineUntilUtc.HasValue,
                        "A cheaper model on the same provider still answers and must NOT be benched");
                    AssertFalse(native!.QuarantineUntilUtc.HasValue,
                        "A captain on another provider must never be benched by this path");
                }
            });

            await RunTest("SpendCapBench KeepsWorkingSiblingOwnership", async () =>
            {
                // The group bench runs while other captains on the capped model may still be executing missions. Their
                // processes keep running, so the bench must not clear their mission, dock or process; they bench
                // themselves when their own run returns the cap. Idle siblings are held immediately.
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Vessel vessel = new Vessel("spend-cap-vessel", "https://github.com/test/spend.git");
                    await db.Vessels.CreateAsync(vessel);
                    Mission running = new Mission("running on the capped model", "still working");
                    running.VesselId = vessel.Id;
                    running.Status = MissionStatusEnum.InProgress;
                    await db.Missions.CreateAsync(running);

                    Captain capped = new Captain("capped-failing");
                    capped.Model = "example-provider/gpt-5.6-luna";
                    Captain idleSibling = new Captain("capped-idle");
                    idleSibling.Model = "example-provider/gpt-5.6-luna";
                    Captain workingSibling = new Captain("capped-working");
                    workingSibling.Model = "example-provider/gpt-5.6-luna";
                    await db.Captains.CreateAsync(capped);
                    await db.Captains.CreateAsync(idleSibling);
                    await db.Captains.CreateAsync(workingSibling);

                    Dock dock = new Dock(vessel.Id);
                    dock.CaptainId = workingSibling.Id;
                    dock.WorktreePath = Path.Combine(Path.GetTempPath(), "armada_spend_cap_wt_" + Guid.NewGuid().ToString("N"));
                    dock.BranchName = "armada/spend-cap/" + running.Id;
                    await db.Docks.CreateAsync(dock);
                    workingSibling.State = CaptainStateEnum.Working;
                    workingSibling.CurrentMissionId = running.Id;
                    workingSibling.CurrentDockId = dock.Id;
                    workingSibling.ProcessId = 9191;
                    await db.Captains.UpdateAsync(workingSibling);

                    System.Reflection.MethodInfo? bench = typeof(AdmiralService).GetMethod(
                        "BenchProviderGroupAsync",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (bench == null) throw new InvalidOperationException("Could not find BenchProviderGroupAsync.");

                    await (Task)bench.Invoke(service, new object[]
                    {
                        capped, "Provider daily spend cap reached.", DateTime.UtcNow.AddHours(25), CancellationToken.None
                    })!;

                    Captain? failing = await db.Captains.ReadAsync(capped.Id);
                    Captain? idle = await db.Captains.ReadAsync(idleSibling.Id);
                    Captain? working = await db.Captains.ReadAsync(workingSibling.Id);

                    AssertEqual(CaptainStateEnum.Quarantined, failing!.State, "The captain that returned the cap is benched");
                    AssertEqual(CaptainStateEnum.Quarantined, idle!.State, "An idle sibling on the capped model is benched");
                    AssertEqual(CaptainStateEnum.Working, working!.State, "A working sibling keeps running");
                    AssertEqual(running.Id, working.CurrentMissionId, "A working sibling keeps its mission");
                    AssertEqual(dock.Id, working.CurrentDockId, "A working sibling keeps its dock");
                    AssertEqual(9191, working.ProcessId, "A working sibling keeps its process");
                }
            });

            await RunTest("GetStatusAsync counts only pending missions waiting for resource pressure", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), new StubGitService());

                    Mission waitingOne = new Mission("Waiting one") { Status = MissionStatusEnum.Pending, AssignmentState = MissionAssignmentStateEnum.WaitingForResourcePressure };
                    Mission waitingTwo = new Mission("Waiting two") { Status = MissionStatusEnum.Pending, AssignmentState = MissionAssignmentStateEnum.WaitingForResourcePressure };
                    Mission waitingForCaptain = new Mission("Waiting captain") { Status = MissionStatusEnum.Pending, AssignmentState = MissionAssignmentStateEnum.WaitingForIdleCaptain };
                    Mission completedStale = new Mission("Completed stale") { Status = MissionStatusEnum.Complete, AssignmentState = MissionAssignmentStateEnum.WaitingForResourcePressure };

                    await db.Missions.CreateAsync(waitingOne);
                    await db.Missions.CreateAsync(waitingTwo);
                    await db.Missions.CreateAsync(waitingForCaptain);
                    await db.Missions.CreateAsync(completedStale);

                    ArmadaStatus status = await service.GetStatusAsync();

                    AssertEqual(2, status.MissionsWaitingForResourcePressure, "Only pending missions whose assignment waits for resource pressure are counted");
                }
            });

            await RunTest("GetStatusAsync IncludesWorkProducedAndLandingFailedAfterLightweightCounts", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Mission produced = new Mission("WP")
                    {
                        Status = MissionStatusEnum.WorkProduced,
                        AgentOutput = new string('o', 32 * 1024),
                        DiffSnapshot = new string('d', 32 * 1024)
                    };
                    Mission landingFailed1 = new Mission("LF1") { Status = MissionStatusEnum.LandingFailed };
                    Mission landingFailed2 = new Mission("LF2") { Status = MissionStatusEnum.LandingFailed };
                    Mission failed = new Mission("F1") { Status = MissionStatusEnum.Failed };

                    await db.Missions.CreateAsync(produced);
                    await db.Missions.CreateAsync(landingFailed1);
                    await db.Missions.CreateAsync(landingFailed2);
                    await db.Missions.CreateAsync(failed);

                    ArmadaStatus status = await service.GetStatusAsync();

                    AssertEqual(1, status.MissionsByStatus["WorkProduced"]);
                    AssertEqual(2, status.MissionsByStatus["LandingFailed"]);
                    AssertEqual(1, status.MissionsByStatus["Failed"]);
                    AssertFalse(status.MissionsByStatus.ContainsKey("Pending"), "Empty buckets should be omitted");
                }
            });

            await RunTest("GetStatusAsync objective and incident counts equal the filtered enumerate totals for every value", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), new StubGitService());
                    AuthContext auth = McpTestCaller.Operator;
                    ObjectiveService objectives = new ObjectiveService(db);
                    IncidentService incidents = new IncidentService(db);

                    ObjectiveStatusEnum[] objectiveStatuses = Enum.GetValues<ObjectiveStatusEnum>();
                    ObjectiveBacklogStateEnum[] backlogStates = Enum.GetValues<ObjectiveBacklogStateEnum>();
                    for (int i = 0; i < 20; i++)
                    {
                        await objectives.CreateAsync(auth, new ObjectiveUpsertRequest
                        {
                            Title = "Counted objective " + i,
                            Status = objectiveStatuses[(i * 5) % objectiveStatuses.Length],
                            BacklogState = backlogStates[i % backlogStates.Length]
                        });
                    }

                    IncidentStatusEnum[] incidentStatuses = Enum.GetValues<IncidentStatusEnum>();
                    IncidentSeverityEnum[] severities = Enum.GetValues<IncidentSeverityEnum>();
                    for (int i = 0; i < 14; i++)
                    {
                        await incidents.CreateAsync(auth, new IncidentUpsertRequest
                        {
                            Title = "Counted incident " + i,
                            Status = i < 6 ? IncidentStatusEnum.Open : incidentStatuses[i % incidentStatuses.Length],
                            Severity = severities[(i * 3) % severities.Length],
                            RootCause = "Written cause for counted incident " + i
                        });
                    }

                    StructuredDeliveryStatus delivery = (await service.GetStatusAsync()).StructuredDelivery;

                    foreach (ObjectiveStatusEnum value in objectiveStatuses)
                    {
                        long total = (await objectives.EnumerateAsync(auth, new ObjectiveQuery { Status = value, PageNumber = 1, PageSize = 1 })).TotalRecords;
                        AssertEqual(total, delivery.ObjectivesByStatus.TryGetValue(value.ToString(), out int count) ? count : 0L, "objectives with status " + value);
                    }
                    foreach (ObjectiveBacklogStateEnum value in backlogStates)
                    {
                        long total = (await objectives.EnumerateAsync(auth, new ObjectiveQuery { BacklogState = value, PageNumber = 1, PageSize = 1 })).TotalRecords;
                        AssertEqual(total, delivery.BacklogByState.TryGetValue(value.ToString(), out int count) ? count : 0L, "objectives in backlog state " + value);
                    }
                    foreach (IncidentStatusEnum value in incidentStatuses)
                    {
                        long total = (await incidents.EnumerateAsync(auth, new IncidentQuery { Status = value, PageNumber = 1, PageSize = 1 })).TotalRecords;
                        AssertEqual(total, delivery.IncidentsByStatus.TryGetValue(value.ToString(), out int count) ? count : 0L, "incidents with status " + value);
                    }
                    foreach (IncidentSeverityEnum value in severities)
                    {
                        long total = (await incidents.EnumerateAsync(auth, new IncidentQuery { Status = IncidentStatusEnum.Open, Severity = value, PageNumber = 1, PageSize = 1 })).TotalRecords;
                        AssertEqual(total, delivery.OpenIncidentsBySeverity.TryGetValue(value.ToString(), out int count) ? count : 0L, "open incidents with severity " + value);
                    }

                    AssertTrue(delivery.ObjectivesByStatus.Count > 1, "the seed spans several objective statuses");
                    AssertTrue(delivery.IncidentsByStatus.Count > 1, "the seed spans several incident statuses");
                    AssertTrue(delivery.OpenIncidentsBySeverity.Count > 1, "the seed spans several open severities");
                    AssertFalse(delivery.ObjectivesByStatus.Values.Any(v => v == 0), "empty buckets are omitted");
                }
            });

            // Dispatch ergonomics: a read-only dispatch (all missions Audit/Research)
            // must NOT inherit a multi-stage vessel default pipeline -- a four-mission diagnostic
            // probe once expanded to sixteen missions. An explicitly requested pipeline still wins.
            await RunTest("DispatchVoyageAsync ReadOnlyMissionsSkipVesselDefaultPipeline", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 2;
                    settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = 2;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Pipeline reviewed = new Pipeline("Reviewed");
                    reviewed.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    reviewed = await db.Pipelines.CreateAsync(reviewed);

                    Vessel vessel = new Vessel("ReadOnlyVessel", "https://github.com/test/repo");
                    vessel.DefaultPipelineId = reviewed.Id;
                    await db.Vessels.CreateAsync(vessel);

                    // All-mission Audit dispatch with no explicit pipeline: single Worker mission.
                    Voyage auditVoyage = await service.DispatchVoyageAsync(
                        "Audit Probe", "diagnostic", vessel.Id,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Probe A", "inspect") { Mode = "Audit" },
                            new MissionDescription("Probe B", "inspect") { Mode = "Audit" }
                        },
                        (string?)null).ConfigureAwait(false);

                    List<Mission> auditMissions = await db.Missions.EnumerateByVoyageAsync(auditVoyage.Id).ConfigureAwait(false);
                    AssertEqual(2, auditMissions.Count, "Read-only dispatch must not expand stages from the vessel default pipeline");
                    AssertTrue(auditMissions.All(m => !String.Equals(m.Persona, "Judge", StringComparison.OrdinalIgnoreCase)),
                        "Read-only dispatch must not create the default pipeline's review stages");

                    // An explicit pipeline is always honored, even for read-only missions.
                    Voyage explicitVoyage = await service.DispatchVoyageAsync(
                        "Audit With Pipeline", "diagnostic", vessel.Id,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Probe C", "inspect") { Mode = "Research" }
                        },
                        reviewed.Id).ConfigureAwait(false);

                    List<Mission> explicitMissions = await db.Missions.EnumerateByVoyageAsync(explicitVoyage.Id).ConfigureAwait(false);
                    AssertEqual(2, explicitMissions.Count, "An explicitly requested pipeline must still expand its stages");
                }
            });

            await RunTest("DispatchVoyageAsync records the dispatched stage list before skips, and pipeline update and delete count the running voyage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), new StubGitService());

                    Pipeline recorded = new Pipeline("RecordedStages") { TenantId = Armada.Core.Constants.DefaultTenantId };
                    recorded.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "TestEngineer"),
                        new PipelineStage(3, "Judge")
                    };
                    recorded = await db.Pipelines.CreateAsync(recorded);

                    Vessel vessel = new Vessel("RecordedStagesVessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    Voyage voyage = await service.DispatchVoyageAsync(
                        "Recorded stages", "dispatch", vessel.Id,
                        new List<MissionDescription> { new MissionDescription("Change", "edit") },
                        recorded.Id,
                        null,
                        new StageSkipRequest { Stages = new List<string> { "TestEngineer" }, Reason = "covered", ConfirmedBy = "operator" }).ConfigureAwait(false);

                    Pipeline? read = await VoyagePipelineRecord.ReadAsync(db, voyage.Id, null).ConfigureAwait(false);
                    AssertNotNull(read, "the dispatch records its stage list on the voyage");
                    AssertEqual(recorded.Id, read!.Id, "the record names the dispatched pipeline");
                    AssertEqual("Worker(1),TestEngineer(2),Judge(3)",
                        String.Join(",", read.Stages.Select(stage => stage.PersonaName + "(" + stage.Order + ")")),
                        "the record holds the resolved list before skips; the skip is recorded on its own");

                    PipelineService pipelines = new PipelineService(db);
                    RecordWriteResult<Pipeline> updated = await pipelines.UpdateAsync(McpTestCaller.Operator, recorded.Name,
                        new PipelineWriteRequest { Description = "edited while a voyage runs" }).ConfigureAwait(false);
                    AssertTrue(updated.Succeeded, "the edit is not refused");
                    AssertEqual(1, updated.Record!.RunningVoyageCount ?? -1, "the update reports the running voyage");

                    voyage.Status = VoyageStatusEnum.Complete;
                    await db.Voyages.UpdateAsync(voyage).ConfigureAwait(false);
                    RecordWriteResult<Pipeline> deleted = await pipelines.DeleteAsync(McpTestCaller.Operator, recorded.Name).ConfigureAwait(false);
                    AssertTrue(deleted.Succeeded, "the delete is not refused");
                    AssertEqual(0, deleted.Record!.RunningVoyageCount ?? -1, "a finished voyage is not counted");
                }
            });

            // Read-only missions still run every declared pipeline stage. Review and verification
            // stages consume the report and must remain in the dependency chain.
            await RunTest("DispatchVoyageAsync ReadOnlyMissionPreservesTheTestEngineerStage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 2;
                    settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = 2;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Pipeline tested = new Pipeline("WorkerTestedJudged");
                    tested.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "TestEngineer"),
                        new PipelineStage(3, "Judge")
                    };
                    tested = await db.Pipelines.CreateAsync(tested);

                    Vessel vessel = new Vessel("StageModeVessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    Voyage auditVoyage = await service.DispatchVoyageAsync(
                        "Audit With Full Pipeline", "diagnostic", vessel.Id,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Probe", "inspect") { Mode = "Audit" }
                        },
                        tested.Id).ConfigureAwait(false);

                    List<Mission> auditMissions = await db.Missions.EnumerateByVoyageAsync(auditVoyage.Id).ConfigureAwait(false);

                    List<Mission> orderedAuditMissions = auditMissions.OrderBy(m => m.StageOrder).ToList();
                    AssertEqual(3, orderedAuditMissions.Count, "Every declared pipeline stage must be materialized for a read-only mission");
                    AssertTrue(
                        orderedAuditMissions.Any(m => String.Equals(m.Persona, "TestEngineer", StringComparison.OrdinalIgnoreCase)),
                        "A TestEngineer verification stage must remain on an Audit mission");
                    AssertNull(orderedAuditMissions[0].DependsOnMissionId, "The first stage must have no dependency");
                    AssertEqual(orderedAuditMissions[0].Id, orderedAuditMissions[1].DependsOnMissionId, "The TestEngineer must depend on Worker");
                    AssertEqual(orderedAuditMissions[1].Id, orderedAuditMissions[2].DependsOnMissionId, "The Judge must depend on TestEngineer");
                    AssertTrue(
                        auditMissions.Any(m => String.Equals(m.Persona, "Judge", StringComparison.OrdinalIgnoreCase)),
                        "The review stage still has a report to read and must survive");

                    Voyage implVoyage = await service.DispatchVoyageAsync(
                        "Implementation With Full Pipeline", "build it", vessel.Id,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Build", "implement") { Mode = "Implementation" }
                        },
                        tested.Id).ConfigureAwait(false);

                    List<Mission> implMissions = await db.Missions.EnumerateByVoyageAsync(implVoyage.Id).ConfigureAwait(false);
                    AssertEqual(3, implMissions.Count, "An implementing mission keeps every stage of its pipeline");
                }
            });

            // A stage that does not inherit the dispatch mode runs as Implementation: the gate then
            // judges report-only work by its commit, and the brief carries instructions the captain
            // cannot follow.
            await RunTest("DispatchVoyageAsync PipelineStagesInheritTheMissionMode", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    Pipeline reviewed = new Pipeline("WorkerJudged");
                    reviewed.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    reviewed = await db.Pipelines.CreateAsync(reviewed);

                    Vessel vessel = new Vessel("ModeInheritVessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    Voyage voyage = await service.DispatchVoyageAsync(
                        "Audit Modes", "diagnostic", vessel.Id,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Probe", "inspect") { Mode = "Audit" }
                        },
                        reviewed.Id).ConfigureAwait(false);

                    List<Mission> missions = await db.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);

                    AssertTrue(missions.Count > 0, "the dispatch must create stages");
                    AssertTrue(
                        missions.All(m => m.Mode == MissionModeEnum.Audit),
                        "every stage of a read-only dispatch must run read-only");
                }
            });

            await RunTest("DispatchVoyageAsync PipelineResolvesStageOverridesForEachPersona", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.ModelTier.CopyFrom(FleetRoutingSettings.CreateModelTier());
                    settings.ModelTier.Records = FleetRoutingSettings.CreateModelTier().Records;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Pipeline pipeline = new Pipeline("PersonaResolvedRouting");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker") { PreferredModel = "high" },
                        new PipelineStage(2, "TestEngineer"),
                        new PipelineStage(3, "Judge")
                    };
                    pipeline = await db.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Vessel vessel = new Vessel("PersonaResolvedVessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Voyage voyage = await service.DispatchVoyageAsync(
                        "Persona resolved routing",
                        "verify persisted stage preferences",
                        vessel.Id,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Implement", "Change code") { PreferredModel = "mid" }
                        },
                        pipeline.Id).ConfigureAwait(false);

                    List<Mission> missions = await db.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertEqual("high", missions.Single(mission => mission.Persona == "Worker").PreferredModel,
                        "a Worker stage override of high is kept: a persona with no minimum tier takes the request as its floor");
                    AssertEqual("mid", missions.Single(mission => mission.Persona == "TestEngineer").PreferredModel,
                        "a Standard minimum tier and an inherited mid request combine to mid");
                    AssertEqual("high", missions.Single(mission => mission.Persona == "Judge").PreferredModel,
                        "a Premium minimum tier raises an inherited mid request to high");
                }
            });

            await RunTest("DispatchVoyageQueuedAsync PipelinePreservesLiteralModelPins", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.ModelTier.CopyFrom(FleetRoutingSettings.CreateModelTier());
                    settings.ModelTier.Records = FleetRoutingSettings.CreateModelTier().Records;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Pipeline pipeline = new Pipeline("LiteralModelRouting");
                    pipeline.Stages = new List<PipelineStage>
                    {
                        new PipelineStage(1, "Worker"),
                        new PipelineStage(2, "Judge")
                    };
                    pipeline = await db.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

                    Vessel vessel = new Vessel("LiteralModelVessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                    Voyage voyage = await service.DispatchVoyageQueuedAsync(
                        "Literal model routing",
                        "keep the operator pin",
                        vessel.Id,
                        new List<MissionDescription>
                        {
                            new MissionDescription("Implement", "Change code") { PreferredModel = "gpt-5.6-luna" }
                        },
                        pipeline.Id,
                        null).ConfigureAwait(false);

                    List<Mission> missions = await db.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    AssertTrue(missions.All(mission => mission.PreferredModel == "gpt-5.6-luna"),
                        "queued pipeline creation must preserve a literal model pin for Worker and Judge stages");
                }
            });

            await RunTest("DispatchVoyageQueuedAsync ReturnsBeforeAssignmentLaunchCompletes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, CreateSettings(), git);

                    TaskCompletionSource<bool> launchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    TaskCompletionSource<int> releaseLaunch = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    service.OnLaunchAgent = (captain, mission, dock) =>
                    {
                        launchStarted.TrySetResult(true);
                        return releaseLaunch.Task;
                    };

                    Vessel vessel = new Vessel("QueuedVessel", "https://github.com/test/repo");
                    vessel.DefaultBranch = "main";
                    await db.Vessels.CreateAsync(vessel);

                    Captain captain = new Captain("queued-captain");
                    captain.State = CaptainStateEnum.Idle;
                    await db.Captains.CreateAsync(captain);

                    Task<Voyage> dispatchTask = service.DispatchVoyageQueuedAsync(
                        "Queued Voyage", "A queued test", vessel.Id,
                        new List<MissionDescription> { new MissionDescription("Queued Mission", "Desc") },
                        null,
                        null);

                    Task completed = await Task.WhenAny(dispatchTask, Task.Delay(1000)).ConfigureAwait(false);
                    AssertTrue(Object.ReferenceEquals(dispatchTask, completed), "Queued dispatch should return before the launch delegate completes");

                    Voyage voyage = await dispatchTask.ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Open, voyage.Status, "Queued voyage should remain Open until background assignment advances it");

                    completed = await Task.WhenAny(launchStarted.Task, Task.Delay(2000)).ConfigureAwait(false);
                    AssertTrue(Object.ReferenceEquals(launchStarted.Task, completed), "Background assignment should still start after durable creation");

                    releaseLaunch.SetResult(424242);

                    Mission? mission = null;
                    for (int i = 0; i < 20; i++)
                    {
                        List<Mission> missions = await db.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                        mission = missions.SingleOrDefault();
                        if (mission != null && mission.Status == MissionStatusEnum.InProgress) break;
                        await Task.Delay(100).ConfigureAwait(false);
                    }

                    AssertNotNull(mission, "Queued mission should exist");
                    AssertEqual(MissionStatusEnum.InProgress, mission!.Status, "Queued mission should finish background assignment once launch completes");
                    AssertEqual(424242, mission.ProcessId, "Queued assignment should persist the launched process id");
                }
            });

            await RunTest("HandleProcessExitAsync RateLimitFailure RequeuesMissionWithoutHaltingVoyage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MaxRecoveryAttempts = 3;
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Voyage voyage = new Voyage("Quota Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    Mission mission = new Mission("Quota Judge");
                    mission.VoyageId = voyage.Id;
                    // No VesselId: drives the deterministic _RetryDispatchNeeded fallback requeue branch
                    // (QueueVoyageAssignments needs a vessel id; its absence avoids a background reassign race).
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.ProcessId = 4242;
                    await db.Missions.CreateAsync(mission);

                    Captain captain = new Captain("quota-judge");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 4242;
                    await db.Captains.CreateAsync(captain);

                    string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                    Directory.CreateDirectory(missionLogDir);
                    await File.WriteAllTextAsync(
                        Path.Combine(missionLogDir, mission.Id + ".log"),
                        "[stderr] You've hit your limit and must wait for reset.\n[2026-04-02 23:49:03] Agent exited with code 1").ConfigureAwait(false);

                    await service.HandleProcessExitAsync(4242, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Captain? updatedCaptain = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertNotNull(updatedCaptain, "Captain should still exist");
                    AssertNotNull(updatedVoyage, "Voyage should still exist");
                    // Old behavior marked the mission Failed and cancelled the voyage; the quota re-route requeues instead.
                    AssertEqual(MissionStatusEnum.Pending, updatedMission!.Status, "Rate-limit (quota) failure should re-route the mission to Pending, not Failed");
                    AssertEqual(MissionAssignmentStateEnum.Pending, updatedMission.AssignmentState, "Requeued mission should reset AssignmentState to Pending");
                    AssertContains("hit your limit", updatedMission.FailureReason ?? String.Empty, "Requeued mission should preserve the failure reason for operator visibility");
                    AssertNull(updatedMission.CaptainId, "Requeued mission should clear the captain binding");
                    AssertNull(updatedMission.ProcessId, "Requeued mission should clear the stale process id");
                    AssertEqual(CaptainStateEnum.Quarantined, updatedCaptain!.State, "Failing captain should be quarantined so the retry selects a different captain");
                    AssertNull(updatedCaptain.CurrentMissionId, "Stalled captain should release its mission assignment");
                    AssertEqual(VoyageStatusEnum.InProgress, updatedVoyage!.Status, "Transient captain failure must NOT halt the voyage");
                    AssertTrue(GetRetryDispatchNeeded(service), "Requeue without a vessel id should flag the retry dispatch sweep");

                    EnumerationResult<ArmadaEvent> events = await db.Events.EnumerateAsync(new EnumerationQuery { PageNumber = 1, PageSize = 100 }).ConfigureAwait(false);
                    AssertFalse(events.Objects.Any(e => e.EventType == "mission.failed" && e.MissionId == mission.Id), "Retryable requeue must not emit mission.failed");
                    AssertTrue(events.Objects.Any(e => e.EventType == "mission.quota_rerouted" && e.MissionId == mission.Id), "A quota re-route should emit a non-terminal mission.quota_rerouted event");
                }
            });

            await RunTest("HandleProcessExitAsync QuotaFailureOnAccountCaptain HoldsWholeAccountExhausted", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MaxRecoveryAttempts = 3;
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    Captain failing = new Captain("account-first") { Model = "shared-model", State = CaptainStateEnum.Working, ProcessId = 5151 };
                    Captain sibling = new Captain("account-second") { Model = "shared-model", State = CaptainStateEnum.Idle };
                    Captain other = new Captain("other-account") { Model = "shared-model", State = CaptainStateEnum.Idle };
                    await db.Captains.CreateAsync(failing);
                    await db.Captains.CreateAsync(sibling);
                    await db.Captains.CreateAsync(other);
                    settings.ModelTier.UsageRouting = new UsageRoutingSettings
                    {
                        Enabled = true,
                        Accounts = new List<UsageAccountSettings>
                        {
                            new UsageAccountSettings { Id = "shared", CaptainIds = new List<string> { failing.Id, sibling.Id } },
                            new UsageAccountSettings { Id = "separate", CaptainIds = new List<string> { other.Id } }
                        },
                        PersonaRoutes = new Dictionary<string, List<UsageRouteSettings>>
                        {
                            ["Worker"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = "shared" }, new UsageRouteSettings { AccountId = "separate" } }
                        }
                    };
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Voyage voyage = new Voyage("Account quota voyage") { Status = VoyageStatusEnum.InProgress };
                    await db.Voyages.CreateAsync(voyage);
                    Mission mission = new Mission("Account quota mission")
                    {
                        VoyageId = voyage.Id,
                        Persona = "Worker",
                        Status = MissionStatusEnum.InProgress,
                        AssignmentState = MissionAssignmentStateEnum.Assigned,
                        ProcessId = 5151
                    };
                    await db.Missions.CreateAsync(mission);
                    failing.CurrentMissionId = mission.Id;
                    await db.Captains.UpdateAsync(failing);

                    string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                    Directory.CreateDirectory(missionLogDir);
                    await File.WriteAllTextAsync(
                        Path.Combine(missionLogDir, mission.Id + ".log"),
                        "[stderr] You've hit your limit and must wait for reset.\n[2026-04-02 23:49:03] Agent exited with code 1").ConfigureAwait(false);

                    await service.HandleProcessExitAsync(5151, 1, failing.Id, mission.Id).ConfigureAwait(false);

                    Captain? failingAfter = await db.Captains.ReadAsync(failing.Id).ConfigureAwait(false);
                    Captain? siblingAfter = await db.Captains.ReadAsync(sibling.Id).ConfigureAwait(false);
                    Captain? otherAfter = await db.Captains.ReadAsync(other.Id).ConfigureAwait(false);
                    Mission? missionAfter = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, missionAfter!.Status, "The mission is re-routed, not failed");
                    AssertEqual(CaptainStateEnum.Quarantined, failingAfter!.State, "The failing captain is benched");
                    AssertEqual(CaptainStateEnum.Quarantined, siblingAfter!.State, "An idle captain on the same account must not receive the re-routed mission");
                    AssertEqual(CaptainStateEnum.Idle, otherAfter!.State, "A captain on a different account stays available");

                    UsageRoutingService usage = UsageRoutingService.For(settings);
                    ProviderUsageStatus status = usage.GetStatus(settings.ModelTier.UsageRouting.Accounts[0], null, DateTime.UtcNow);
                    AssertEqual("Exhausted", status.State, "The whole account is Exhausted");
                    AssertEqual("account_provider_failure", status.Reason);
                    AssertEqual(UsageRoutingService.HoldKindQuota, status.HoldKind, "The hold names its kind");
                    AssertTrue(status.ExhaustedUntilUtc > DateTime.UtcNow, "The hold lasts until the retry time");
                    AssertFalse(usage.GetStatus(settings.ModelTier.UsageRouting.Accounts[1], null, DateTime.UtcNow).State == "Exhausted", "The other account is not held");

                    // Routing refuses the account even for a sibling that is idle and unbenched, for example one that
                    // finished a running mission after the failure.
                    Captain freed = new Captain("account-second") { Id = sibling.Id, Model = "shared-model", State = CaptainStateEnum.Idle };
                    UsageRoutingDecision decision = SmartRoutingTestSelect.Select(usage, settings.ModelTier.UsageRouting, new Mission { Persona = "Worker" },
                        new List<Captain> { freed, otherAfter }, Array.Empty<string>(), DateTime.UtcNow);
                    AssertEqual(1, decision.Candidates.Count, "Only the captain on the other account is a candidate");
                    AssertEqual(other.Id, decision.Candidates[0].Id);
                }
            });

            await RunTest("HandleProcessExitAsync QuotaHoldFromOneError IsCappedAndEndsWhenAnotherCaptainOnTheAccountFinishesARun", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MaxRecoveryAttempts = 3;
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    Captain failing = new Captain("hold-source") { Model = "shared-model", State = CaptainStateEnum.Working, ProcessId = 5353 };
                    Captain idleSibling = new Captain("hold-idle-sibling") { Model = "shared-model", State = CaptainStateEnum.Idle };
                    Captain busySibling = new Captain("hold-busy-sibling") { Model = "shared-model", State = CaptainStateEnum.Working, ProcessId = 5454 };
                    await db.Captains.CreateAsync(failing);
                    await db.Captains.CreateAsync(idleSibling);
                    await db.Captains.CreateAsync(busySibling);
                    settings.ModelTier.UsageRouting = new UsageRoutingSettings
                    {
                        Enabled = true,
                        Accounts = new List<UsageAccountSettings>
                        {
                            new UsageAccountSettings { Id = "shared", CaptainIds = new List<string> { failing.Id, idleSibling.Id, busySibling.Id } }
                        }
                    };
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Voyage voyage = new Voyage("Account hold voyage") { Status = VoyageStatusEnum.InProgress };
                    await db.Voyages.CreateAsync(voyage);
                    Mission failed = new Mission("Mission that hits the limit")
                    {
                        VoyageId = voyage.Id, Persona = "Worker", Status = MissionStatusEnum.InProgress,
                        AssignmentState = MissionAssignmentStateEnum.Assigned, ProcessId = 5353, CaptainId = failing.Id
                    };
                    Mission served = new Mission("Mission the provider serves")
                    {
                        VoyageId = voyage.Id, Persona = "Worker", Status = MissionStatusEnum.InProgress,
                        AssignmentState = MissionAssignmentStateEnum.Assigned, ProcessId = 5454, CaptainId = busySibling.Id
                    };
                    await db.Missions.CreateAsync(failed);
                    await db.Missions.CreateAsync(served);
                    failing.CurrentMissionId = failed.Id;
                    busySibling.CurrentMissionId = served.Id;
                    await db.Captains.UpdateAsync(failing);
                    await db.Captains.UpdateAsync(busySibling);

                    string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                    Directory.CreateDirectory(missionLogDir);
                    await File.WriteAllTextAsync(
                        Path.Combine(missionLogDir, failed.Id + ".log"),
                        "[stderr] ActionRequiredError: Weekly usage limit reached. It resets in 5 days.\n[2026-04-02 23:49:03] Agent exited with code 1").ConfigureAwait(false);

                    // The default account hold limit.
                    const int DefaultAccountHoldMinutes = 60;
                    DateTime before = DateTime.UtcNow;
                    await service.HandleProcessExitAsync(5353, 1, failing.Id, failed.Id).ConfigureAwait(false);

                    Captain? sourceHeld = await db.Captains.ReadAsync(failing.Id).ConfigureAwait(false);
                    Captain? siblingHeld = await db.Captains.ReadAsync(idleSibling.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Quarantined, siblingHeld!.State, "the idle sibling is held with the account");
                    AssertTrue(siblingHeld.QuarantineUntilUtc.HasValue
                        && siblingHeld.QuarantineUntilUtc.Value <= before.AddMinutes(DefaultAccountHoldMinutes + 1),
                        "one error holds a sibling no longer than the account hold limit, not the provider's 5 days: " + siblingHeld.QuarantineUntilUtc);
                    AssertContains(failing.Id, siblingHeld.QuarantineReason ?? String.Empty, "the sibling's hold names the captain whose error started it");
                    AssertTrue(sourceHeld!.QuarantineUntilUtc > before.AddDays(1), "the failing captain keeps the provider's own retry time");
                    UsageRoutingService usage = UsageRoutingService.For(settings);
                    ProviderUsageStatus held = usage.GetStatus(settings.ModelTier.UsageRouting.Accounts[0], null, DateTime.UtcNow);
                    AssertEqual("Exhausted", held.State, "the account is held");
                    AssertTrue(held.ExhaustedUntilUtc <= before.AddMinutes(DefaultAccountHoldMinutes + 1), "the account hold is capped");

                    // The busy sibling's run ends cleanly: the provider served the account after the error.
                    await service.HandleProcessExitAsync(5454, 0, busySibling.Id, served.Id).ConfigureAwait(false);

                    Captain? sourceAfter = await db.Captains.ReadAsync(failing.Id).ConfigureAwait(false);
                    Captain? siblingAfter = await db.Captains.ReadAsync(idleSibling.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Idle, siblingAfter!.State, "a served run on the account releases the held sibling");
                    AssertEqual(CaptainStateEnum.Idle, sourceAfter!.State, "a served run on the account releases the captain whose error started the hold");
                    AssertFalse(usage.GetStatus(settings.ModelTier.UsageRouting.Accounts[0], null, DateTime.UtcNow).State == "Exhausted", "the account hold is cleared");
                    EnumerationResult<ArmadaEvent> events = await db.Events.EnumerateAsync(new EnumerationQuery { PageNumber = 1, PageSize = 100 }).ConfigureAwait(false);
                    ArmadaEvent? cleared = events.Objects.FirstOrDefault(e => e.EventType == "usage_account.hold_cleared");
                    AssertNotNull(cleared, "the operator sees the hold end");
                    AssertContains(busySibling.Id, cleared!.Message, "the event names the captain whose run ended the hold");
                }
            });

            await RunTest("HandleProcessExitAsync AuthFailureOnAccountCaptain RefusesAccountUntilSignInWithoutTimedHold", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MaxRecoveryAttempts = 3;
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));

                    Captain failing = new Captain("account-first") { Model = "shared-model", State = CaptainStateEnum.Working, ProcessId = 5252 };
                    Captain sibling = new Captain("account-second") { Model = "shared-model", State = CaptainStateEnum.Idle };
                    Captain other = new Captain("other-account") { Model = "shared-model", State = CaptainStateEnum.Idle };
                    await db.Captains.CreateAsync(failing);
                    await db.Captains.CreateAsync(sibling);
                    await db.Captains.CreateAsync(other);
                    settings.ModelTier.UsageRouting = new UsageRoutingSettings
                    {
                        Enabled = true,
                        Accounts = new List<UsageAccountSettings>
                        {
                            new UsageAccountSettings { Id = "shared", CaptainIds = new List<string> { failing.Id, sibling.Id } },
                            new UsageAccountSettings { Id = "separate", CaptainIds = new List<string> { other.Id } }
                        },
                        PersonaRoutes = new Dictionary<string, List<UsageRouteSettings>>
                        {
                            ["Worker"] = new List<UsageRouteSettings> { new UsageRouteSettings { AccountId = "shared" }, new UsageRouteSettings { AccountId = "separate" } }
                        }
                    };
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Voyage voyage = new Voyage("Account login voyage") { Status = VoyageStatusEnum.InProgress };
                    await db.Voyages.CreateAsync(voyage);
                    Mission mission = new Mission("Account login mission")
                    {
                        VoyageId = voyage.Id,
                        Persona = "Worker",
                        Status = MissionStatusEnum.InProgress,
                        AssignmentState = MissionAssignmentStateEnum.Assigned,
                        ProcessId = 5252
                    };
                    await db.Missions.CreateAsync(mission);
                    failing.CurrentMissionId = mission.Id;
                    await db.Captains.UpdateAsync(failing);

                    string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                    Directory.CreateDirectory(missionLogDir);
                    await File.WriteAllTextAsync(
                        Path.Combine(missionLogDir, mission.Id + ".log"),
                        "[stderr] unexpected status 401 Unauthorized: Your refresh token was revoked. Please sign in again.\n[2026-04-02 23:49:03] Agent exited with code 1").ConfigureAwait(false);

                    await service.HandleProcessExitAsync(5252, 1, failing.Id, mission.Id).ConfigureAwait(false);

                    Mission? missionAfter = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Captain? siblingAfter = await db.Captains.ReadAsync(sibling.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, missionAfter!.Status, "The mission is re-routed, not failed");
                    AssertEqual(CaptainStateEnum.Idle, siblingAfter!.State, "A sibling is not quarantined on a timer; the refusal lives on the account");

                    UsageRoutingService usage = UsageRoutingService.For(settings);
                    ProviderUsageStatus status = usage.GetStatus(settings.ModelTier.UsageRouting.Accounts[0], null, DateTime.UtcNow);
                    AssertEqual(AccountLoginProbe.ReasonLoginRejected, status.Reason, "The account needs sign-in, not a quota wait");
                    AssertEqual(UsageRoutingService.HoldKindAuth, status.HoldKind);
                    AssertNull(status.ExhaustedUntilUtc, "No timed quota hold is started");
                    AssertEqual(AccountLoginProbe.ReasonLoginRejected, usage.GetStatus(settings.ModelTier.UsageRouting.Accounts[0], null, DateTime.UtcNow.AddSeconds(settings.CaptainQuarantine.DefaultBackoffSeconds + 60)).Reason,
                        "The refusal outlasts the default backoff, so the next mission is not routed to the dead login");
                    AssertFalse(usage.GetStatus(settings.ModelTier.UsageRouting.Accounts[1], null, DateTime.UtcNow).State == "Exhausted", "The other account is not refused");

                    UsageRoutingDecision decision = SmartRoutingTestSelect.Select(usage, settings.ModelTier.UsageRouting, new Mission { Persona = "Worker" },
                        new List<Captain> { siblingAfter, other }, Array.Empty<string>(), DateTime.UtcNow);
                    AssertEqual(1, decision.Candidates.Count, "Only the captain on the other account is a candidate");
                    AssertEqual(other.Id, decision.Candidates[0].Id);

                    EnumerationResult<ArmadaEvent> events = await db.Events.EnumerateAsync(new EnumerationQuery { PageNumber = 1, PageSize = 100 }).ConfigureAwait(false);
                    ArmadaEvent? rejected = events.Objects.FirstOrDefault(e => e.EventType == AdmiralService.UsageAccountLoginRejectedEvent);
                    AssertNotNull(rejected, "The operator sees an event for the account");
                    AssertEqual("shared", rejected!.EntityId);
                    AssertTrue(rejected.Message.Contains("needs sign-in", StringComparison.Ordinal), "The event says the account needs sign-in: " + rejected.Message);
                    AssertTrue(events.Objects.Any(e => e.EventType == "mission.auth_rerouted" && e.MissionId == mission.Id), "The mission re-route is recorded");
                }
            });

            await RunTest("HealthCheckAsync a step that throws does not stop the steps after it", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    LoggingModule logging = CreateLogging();
                    ArmadaSettings settings = CreateSettings();
                    StubGitService git = new StubGitService();
                    IDockService dockService = new DockService(logging, db, settings, git);
                    ICaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
                    IMissionService missionService = new MissionService(logging, db, settings, dockService, captainService, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
                    RecordingEscalationService escalation = new RecordingEscalationService();
                    AdmiralService service = new AdmiralService(logging, db, settings, captainService, missionService,
                        new VoyageService(logging, db), dockService,
                        escalation: escalation,
                        captainQuarantine: new ThrowingRestoreQuarantineService(new CaptainQuarantineService(db, settings, logging)),
                        git: git);
                    int mergeReconciles = 0;
                    service.OnReconcileMergeEntries = () => { mergeReconciles++; return Task.FromResult(0); };

                    // The first step throws on every cycle, as a poison row would.
                    await service.HealthCheckAsync();
                    await service.HealthCheckAsync();

                    AssertEqual(2, mergeReconciles, "the merge-entry reconciliation later in the chain must run on each cycle");
                    AssertEqual(2, escalation.Evaluations, "escalation, the last step, must run on each cycle");
                    AssertTrue(service.HealthCheckStepFailures.TryGetValue("quarantine restore", out long failures),
                        "the failing step must be counted by name");
                    AssertEqual(2L, failures, "each failure of the step must be counted");
                }
            });

            await RunTest("HealthCheckAsync CompletionVerificationMissingPid RequeuesMissionAndLeavesSiblingsUntouched", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Voyage voyage = new Voyage("Missing PID Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    voyage = await db.Voyages.CreateAsync(voyage);

                    Mission mission = new Mission("Missing PID Mission");
                    mission.VoyageId = voyage.Id;
                    // No VesselId: keeps the requeue on the deterministic _RetryDispatchNeeded fallback branch.
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.ProcessId = null;
                    mission.StartedUtc = DateTime.UtcNow.AddMinutes(-1);
                    mission = await db.Missions.CreateAsync(mission);

                    // Sibling already finished its work; the requeue must leave it alone (no voyage halt).
                    Mission sibling = new Mission("Sibling WorkProduced");
                    sibling.VoyageId = voyage.Id;
                    sibling.Status = MissionStatusEnum.WorkProduced;
                    sibling.LastUpdateUtc = DateTime.UtcNow;
                    sibling = await db.Missions.CreateAsync(sibling);

                    Captain captain = new Captain("missing-pid-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = null;
                    await db.Captains.CreateAsync(captain);

                    await service.HealthCheckAsync();

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id);
                    Mission? updatedSibling = await db.Missions.ReadAsync(sibling.Id);
                    Captain? updatedCaptain = await db.Captains.ReadAsync(captain.Id);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id);

                    AssertNotNull(updatedMission, "Mission should still exist");
                    // Old behavior failed the mission loudly and cancelled the voyage; completion-verification
                    // failures are now treated as transient orchestration faults and requeued.
                    AssertEqual(MissionStatusEnum.Pending, updatedMission!.Status, "Completion-verification (missing PID) failure should requeue the mission to Pending");
                    AssertEqual(MissionAssignmentStateEnum.Pending, updatedMission.AssignmentState, "Requeued mission should reset AssignmentState to Pending");
                    AssertContains("agent completion cannot be verified", updatedMission.FailureReason ?? String.Empty, "Requeue should record the completion-verification reason");
                    AssertNull(updatedMission.CaptainId, "Requeued mission should clear the captain binding");
                    AssertEqual(CaptainStateEnum.Stalled, updatedCaptain!.State, "Failing captain should be benched (Stalled) after a completion-verification fault");
                    AssertNull(updatedCaptain.CurrentMissionId, "Stalled captain should release its mission assignment");
                    AssertEqual(VoyageStatusEnum.InProgress, updatedVoyage!.Status, "Voyage must remain InProgress when a mission is requeued");
                    AssertNotNull(updatedSibling, "Sibling should still exist");
                    AssertEqual(MissionStatusEnum.WorkProduced, updatedSibling!.Status, "Sibling WorkProduced mission must be untouched by the requeue");
                }
            });

            await RunTest("HealthCheckAsync FreshActiveMissionWithoutProcessId SkipsMissingPidFailure", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.LaunchProcessIdGraceSeconds = 30;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Voyage voyage = new Voyage("Fresh Missing PID Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    voyage = await db.Voyages.CreateAsync(voyage);

                    Mission mission = new Mission("Fresh Missing PID Mission");
                    mission.VoyageId = voyage.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.ProcessId = null;
                    mission.StartedUtc = DateTime.UtcNow;
                    mission = await db.Missions.CreateAsync(mission);

                    Captain captain = new Captain("fresh-missing-pid-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = null;
                    await db.Captains.CreateAsync(captain);

                    await service.HealthCheckAsync();

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id);
                    Captain? updatedCaptain = await db.Captains.ReadAsync(captain.Id);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id);

                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.InProgress, updatedMission!.Status, "Fresh active mission without a PID should remain active inside launch grace");
                    AssertNull(updatedMission.FailureReason, "Fresh active mission should not receive a missing-PID failure reason inside launch grace");
                    AssertEqual(CaptainStateEnum.Working, updatedCaptain!.State, "Captain should remain working while launch PID registration is still fresh");
                    AssertEqual(VoyageStatusEnum.InProgress, updatedVoyage!.Status, "Voyage should remain active while missing-PID grace applies");
                }
            });

            await RunTest("HealthCheckAsync StageWatchdogFailsStaleAssignedMissionWithoutCaptainHeartbeat", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.StageWatchdogTimeoutMinutes = 5;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Mission mission = new Mission("Stale stage mission");
                    mission.Status = MissionStatusEnum.Assigned;
                    mission.CaptainId = null;
                    mission.ProcessId = null;
                    mission = await db.Missions.CreateAsync(mission);
                    await SetMissionLastUpdateUtcAsync(testDb, mission.Id, DateTime.UtcNow.AddMinutes(-6));

                    await service.HealthCheckAsync();

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id);
                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.Failed, updatedMission!.Status, "Stage watchdog should fail stale stage missions without a captain/process heartbeat");
                    AssertEqual("stage_watchdog_no_captain_heartbeat", updatedMission.FailureReason);

                    EnumerationResult<Signal> signals = await db.Signals.EnumerateAsync(new EnumerationQuery { PageNumber = 1, PageSize = 10 }).ConfigureAwait(false);
                    AssertEqual(1, signals.Objects.Count, "Stage watchdog should emit an operator-visible error signal");
                    AssertEqual(SignalTypeEnum.Error, signals.Objects[0].Type);
                    AssertContains("stage_watchdog_no_captain_heartbeat", signals.Objects[0].Payload);

                    ProductionFactPage<MissionAttemptFact> facts = await db.MissionAttemptFacts.EnumerateAsync(
                        new ProductionFactQuery { FromUtc = DateTime.UtcNow.AddMinutes(-1), ToUtc = DateTime.UtcNow.AddMinutes(1) }).ConfigureAwait(false);
                    MissionAttemptFact? failedFact = facts.Items.FirstOrDefault(item => item.MissionId == mission.Id
                        && item.FactType == MissionAttemptFactTypeEnum.Failed);
                    AssertNotNull(failedFact, "The watchdog failure emits an attempt fact");
                    AssertEqual("stage_watchdog_no_captain_heartbeat", failedFact!.ReasonCode,
                        "The typed watchdog cause is preserved on the fact");
                }
            });

            await RunTest("HealthCheckAsync StageWatchdogDoesNotFailStaleWorkProducedMission", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.StageWatchdogTimeoutMinutes = 5;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Mission mission = new Mission("Completed handoff mission");
                    mission.Status = MissionStatusEnum.WorkProduced;
                    mission.CaptainId = null;
                    mission.ProcessId = null;
                    mission = await db.Missions.CreateAsync(mission);
                    await SetMissionLastUpdateUtcAsync(testDb, mission.Id, DateTime.UtcNow.AddMinutes(-60));

                    await service.HealthCheckAsync();

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id);
                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.WorkProduced, updatedMission!.Status, "Stage watchdog must not fail post-work missions after captain release");
                    AssertNull(updatedMission.FailureReason, "Post-work mission should not receive a watchdog failure reason");

                    EnumerationResult<Signal> signals = await db.Signals.EnumerateAsync(new EnumerationQuery { PageNumber = 1, PageSize = 10 }).ConfigureAwait(false);
                    AssertEqual(0, signals.Objects.Count, "Stage watchdog should not emit error signals for stale WorkProduced missions");
                }
            });

            await RunTest("HandleProcessExitAsync InvalidModelFailure RequeuesViaQueueVoyageAssignments", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Voyage voyage = new Voyage("Model Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    // Real vessel (FK target) but no idle captains, so the QueueVoyageAssignments branch
                    // is taken and its background reassign attempt is a deterministic no-op.
                    Vessel vessel = new Vessel("Requeue Vessel", "https://github.com/test/repo");
                    await db.Vessels.CreateAsync(vessel);

                    Mission mission = new Mission("Invalid Model Mission");
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vessel.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    mission.ProcessId = 7100;
                    await db.Missions.CreateAsync(mission);

                    Captain captain = new Captain("invalid-model-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 7100;
                    await db.Captains.CreateAsync(captain);

                    string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                    Directory.CreateDirectory(missionLogDir);
                    await File.WriteAllTextAsync(
                        Path.Combine(missionLogDir, mission.Id + ".log"),
                        "[stderr] invalid model: foo-bar-9000 is not a recognized model\n[2026-04-02 23:49:03] Agent exited with code 1").ConfigureAwait(false);

                    await service.HandleProcessExitAsync(7100, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Captain? updatedCaptain = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.Pending, updatedMission!.Status, "Invalid-model (captain-unavailable) failure should requeue the mission to Pending");
                    AssertContains("invalid model", updatedMission.FailureReason ?? String.Empty, "Requeued mission should preserve the model failure reason");
                    AssertEqual(CaptainStateEnum.Stalled, updatedCaptain!.State, "Failing captain should be benched (Stalled) after an invalid-model fault");
                    AssertEqual(VoyageStatusEnum.InProgress, updatedVoyage!.Status, "Invalid-model failure must NOT halt the voyage");
                    AssertFalse(GetRetryDispatchNeeded(service), "Requeue with a vessel id should use QueueVoyageAssignments, not the retry-sweep fallback flag");
                }
            });

            await RunTest("HandleProcessExitAsync GenuineUnrecoverableFailure HaltsVoyageAndCancelsNonTerminalSiblings", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Voyage voyage = new Voyage("Doomed Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    // No mission log file: the reason falls back to a generic non-transient exit message.
                    Mission mission = new Mission("Genuine Failure Mission");
                    mission.VoyageId = voyage.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.ProcessId = 7200;
                    await db.Missions.CreateAsync(mission);

                    Mission activeSibling = new Mission("Active Sibling");
                    activeSibling.VoyageId = voyage.Id;
                    activeSibling.Status = MissionStatusEnum.InProgress;
                    await db.Missions.CreateAsync(activeSibling);

                    Mission producedSibling = new Mission("Produced Sibling");
                    producedSibling.VoyageId = voyage.Id;
                    producedSibling.Status = MissionStatusEnum.WorkProduced;
                    await db.Missions.CreateAsync(producedSibling);

                    Captain captain = new Captain("genuine-failure-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 7200;
                    await db.Captains.CreateAsync(captain);

                    await service.HandleProcessExitAsync(7200, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Mission? updatedActiveSibling = await db.Missions.ReadAsync(activeSibling.Id).ConfigureAwait(false);
                    Mission? updatedProducedSibling = await db.Missions.ReadAsync(producedSibling.Id).ConfigureAwait(false);
                    Captain? updatedCaptain = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                    AssertNotNull(updatedMission, "Mission should still exist");
                    AssertEqual(MissionStatusEnum.Failed, updatedMission!.Status, "A genuine unrecoverable failure should still mark the mission Failed");
                    AssertContains("exited with code", updatedMission.FailureReason ?? String.Empty, "Failed mission should record the generic exit reason");
                    AssertEqual(VoyageStatusEnum.Cancelled, updatedVoyage!.Status, "Genuine unrecoverable failure should still halt the voyage");
                    AssertEqual(MissionStatusEnum.Cancelled, updatedActiveSibling!.Status, "Non-terminal siblings should be cancelled when the voyage halts");
                    AssertEqual(MissionStatusEnum.WorkProduced, updatedProducedSibling!.Status, "WorkProduced siblings are terminal and must not be cancelled by the halt");
                    AssertEqual(CaptainStateEnum.Idle, updatedCaptain!.State, "A non-captain-unavailable failure should release the captain to Idle, not Stalled");

                    EnumerationResult<ArmadaEvent> events = await db.Events.EnumerateAsync(new EnumerationQuery { PageNumber = 1, PageSize = 100 }).ConfigureAwait(false);
                    AssertTrue(events.Objects.Any(e => e.EventType == "mission.failed" && e.MissionId == mission.Id), "Genuine failure should emit mission.failed");

                    ProductionFactPage<MissionAttemptFact> facts = await db.MissionAttemptFacts.EnumerateAsync(
                        new ProductionFactQuery { FromUtc = DateTime.UtcNow.AddMinutes(-1), ToUtc = DateTime.UtcNow.AddMinutes(1) }).ConfigureAwait(false);
                    MissionAttemptFact? failedFact = facts.Items.FirstOrDefault(item => item.MissionId == mission.Id
                        && item.FactType == MissionAttemptFactTypeEnum.Failed);
                    AssertNotNull(failedFact, "The process-exit failure emits an attempt fact");
                    AssertEqual("runtime_crash", failedFact!.ReasonCode,
                        "The typed runtime crash cause is preserved on the fact");
                }
            });

            // Autonomous recovery never selects a mission whose voyage is Cancelled, and the halt above
            // cancels the voyage. The terminal exit path therefore owns the incident for this failure;
            // otherwise the failure is recorded nowhere an operator triages.
            await RunTest("HandleProcessExitAsync GenuineUnrecoverableFailure OpensIncidentForHaltedVoyage", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                    Voyage voyage = new Voyage("Incident Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    Mission mission = new Mission("Incident Mission");
                    mission.VoyageId = voyage.Id;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.ProcessId = 7300;
                    await db.Missions.CreateAsync(mission);

                    Captain captain = new Captain("incident-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 7300;
                    await db.Captains.CreateAsync(captain);

                    await service.HandleProcessExitAsync(7300, 1, captain.Id, mission.Id).ConfigureAwait(false);
                    await service.HandleProcessExitAsync(7300, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    EnumerationResult<Incident> incidents = await new IncidentService(db).EnumerateAsync(
                        Armada.Test.Unit.TestHelpers.McpTestCaller.Operator,
                        new IncidentQuery { MissionId = mission.Id, PageNumber = 1, PageSize = 25 }).ConfigureAwait(false);
                    List<Incident> open = incidents.Objects.Where(i => i.Status != IncidentStatusEnum.Closed).ToList();
                    AssertEqual(1, open.Count, "a genuine terminal exit that halts its voyage must open exactly one incident");
                    AssertEqual(voyage.Id, open[0].VoyageId, "the incident names the halted voyage");
                    AssertContains("exited with code", open[0].RootCause ?? String.Empty, "the incident carries the failure reason");
                }
            });

            // A mission that failed with a non-zero exit but whose
            // captain branch exists in the vessel bare repo holds RECOVERABLE committed work. The
            // branch must be preserved (no reap), the voyage must NOT be cascade-cancelled, and only
            // the missions that directly depend on the failed mission are cancelled. Independent
            // siblings keep running so their work is not destroyed by the failure.
            await RunTest("HandleProcessExitAsync RecoverableWorkFailure PreservesBranchAndLeavesVoyageRunning", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    StubGitService git = new StubGitService();
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, git);

                    Vessel vessel = new Vessel("Recoverable Vessel", "https://github.com/test/repo.git");
                    vessel.LocalPath = Path.Combine(settings.ReposDirectory, "recoverable.git");
                    await db.Vessels.CreateAsync(vessel);

                    Voyage voyage = new Voyage("Recoverable Voyage");
                    voyage.Status = VoyageStatusEnum.InProgress;
                    await db.Voyages.CreateAsync(voyage);

                    string branchName = "armada/claude-1/msn_recoverable";
                    git.ExistingBranches.Add(branchName);

                    Mission mission = new Mission("Recoverable Failure Mission");
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vessel.Id;
                    mission.BranchName = branchName;
                    mission.Status = MissionStatusEnum.InProgress;
                    mission.ProcessId = 7300;
                    await db.Missions.CreateAsync(mission);

                    Mission dependent = new Mission("Dependent Stage");
                    dependent.VoyageId = voyage.Id;
                    dependent.DependsOnMissionId = mission.Id;
                    dependent.Status = MissionStatusEnum.Pending;
                    await db.Missions.CreateAsync(dependent);

                    Mission independentSibling = new Mission("Independent Sibling");
                    independentSibling.VoyageId = voyage.Id;
                    independentSibling.Status = MissionStatusEnum.InProgress;
                    await db.Missions.CreateAsync(independentSibling);

                    Captain captain = new Captain("recoverable-failure-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 7300;
                    await db.Captains.CreateAsync(captain);

                    await service.HandleProcessExitAsync(7300, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Mission? updatedDependent = await db.Missions.ReadAsync(dependent.Id).ConfigureAwait(false);
                    Mission? updatedSibling = await db.Missions.ReadAsync(independentSibling.Id).ConfigureAwait(false);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);

                    AssertEqual(MissionStatusEnum.Failed, updatedMission!.Status, "The mission itself must still be marked Failed.");
                    AssertContains("recoverable work preserved", updatedMission.FailureReason ?? String.Empty, "The failure reason must name the preserved branch.");
                    AssertEqual(VoyageStatusEnum.InProgress, updatedVoyage!.Status, "The voyage must NOT be cascade-cancelled when the failed mission has recoverable work.");
                    AssertEqual(MissionStatusEnum.Cancelled, updatedDependent!.Status, "A direct dependent of the failed mission must be cancelled (it cannot run without the upstream).");
                    AssertEqual(MissionStatusEnum.InProgress, updatedSibling!.Status, "An independent sibling must keep running when the failed mission has recoverable work.");
                    AssertFalse(
                        git.DeleteBranchCalls.Any(call => call.EndsWith(":" + branchName, StringComparison.Ordinal)),
                        "The recoverable captain branch must not be reaped.");

                    EnumerationResult<ArmadaEvent> events = await db.Events.EnumerateAsync(new EnumerationQuery { PageNumber = 1, PageSize = 100 }).ConfigureAwait(false);
                    AssertTrue(events.Objects.Any(e => e.EventType == "mission.failed_recoverable_work" && e.MissionId == mission.Id), "Recoverable-work failure should emit mission.failed_recoverable_work");
                }
            });

            await RunTest("HandleProcessExitAsync InterruptedExit RedispatchesMissionAndKeepsVoyageRunning", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                    Voyage voyage = await db.Voyages.CreateAsync(new Voyage("Interrupted voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    Mission mission = await db.Missions.CreateAsync(new Mission("Interrupted mission")
                    {
                        VoyageId = voyage.Id,
                        Status = MissionStatusEnum.InProgress,
                        AssignmentState = MissionAssignmentStateEnum.Assigned,
                        ProcessId = 7400,
                        StartedUtc = DateTime.UtcNow.AddSeconds(-5)
                    }).ConfigureAwait(false);
                    Captain captain = new Captain("interrupted-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 7400;
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await service.HandleProcessExitAsync(7400, -1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    Captain? updatedCaptain = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    List<ArmadaEvent> events = await db.Events.EnumerateByMissionAsync(mission.Id, 100).ConfigureAwait(false);

                    AssertEqual(MissionStatusEnum.Pending, updatedMission!.Status, "an interrupted exit re-dispatches the mission");
                    AssertNull(updatedMission.CaptainId, "the re-dispatched mission is unbound from its captain");
                    AssertContains("interrupted", updatedMission.FailureReason ?? String.Empty, "the re-dispatch records why the run ended");
                    AssertEqual(VoyageStatusEnum.InProgress, updatedVoyage!.Status, "an interrupted exit does not halt the voyage");
                    AssertEqual(CaptainStateEnum.Idle, updatedCaptain!.State, "an interruption is not a captain fault, so the captain is released, not stalled");
                    AssertEqual(1, events.Count(e => e.EventType == "mission.interrupted_redispatched"), "the re-dispatch emits one named event");
                    AssertFalse(events.Any(e => e.EventType == "mission.failed"), "an interrupted exit emits no mission failure, so recovery opens no rescue");
                }
            });

            await RunTest("HandleProcessExitAsync NativeCrashExitCode IsNotAnInterruption", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                    Voyage voyage = await db.Voyages.CreateAsync(new Voyage("Native crash voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    Mission mission = await db.Missions.CreateAsync(new Mission("Native crash mission")
                    {
                        VoyageId = voyage.Id,
                        Status = MissionStatusEnum.InProgress,
                        AssignmentState = MissionAssignmentStateEnum.Assigned,
                        ProcessId = 7402,
                        StartedUtc = DateTime.UtcNow.AddSeconds(-5)
                    }).ConfigureAwait(false);
                    Captain captain = new Captain("native-crash-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 7402;
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    // A Windows process that crashes with an access violation exits with NTSTATUS 0xC0000005, which
                    // reads as a negative signed code. It is a failure of the run, not a cancellation.
                    await service.HandleProcessExitAsync(7402, -1073741819, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    List<ArmadaEvent> events = await db.Events.EnumerateByMissionAsync(mission.Id, 100).ConfigureAwait(false);

                    AssertEqual(MissionStatusEnum.Failed, updatedMission!.Status, "a native crash exit fails the mission");
                    AssertFalse(events.Any(e => e.EventType == "mission.interrupted_redispatched"), "a native crash exit is never re-dispatched as an interruption");
                }
            });

            await RunTest("HandleProcessExitAsync InterruptedExit BudgetExhausted FailsTerminally", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                    Voyage voyage = await db.Voyages.CreateAsync(new Voyage("Repeatedly interrupted voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    Mission mission = await db.Missions.CreateAsync(new Mission("Repeatedly interrupted mission")
                    {
                        VoyageId = voyage.Id,
                        Status = MissionStatusEnum.InProgress,
                        AssignmentState = MissionAssignmentStateEnum.Assigned,
                        ProcessId = 7401,
                        StartedUtc = DateTime.UtcNow.AddSeconds(-5)
                    }).ConfigureAwait(false);
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        ArmadaEvent prior = new ArmadaEvent("mission.interrupted_redispatched", "prior re-dispatch " + attempt);
                        prior.MissionId = mission.Id;
                        prior.VoyageId = voyage.Id;
                        await db.Events.CreateAsync(prior).ConfigureAwait(false);
                    }
                    Captain captain = new Captain("repeatedly-interrupted-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 7401;
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    await service.HandleProcessExitAsync(7401, -1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    Voyage? updatedVoyage = await db.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                    List<ArmadaEvent> events = await db.Events.EnumerateByMissionAsync(mission.Id, 100).ConfigureAwait(false);

                    AssertEqual(MissionStatusEnum.Failed, updatedMission!.Status, "an interruption past the default budget of two re-dispatches fails the mission");
                    AssertEqual(VoyageStatusEnum.Cancelled, updatedVoyage!.Status, "the exhausted mission halts the voyage like any other failure");
                    AssertEqual(2, events.Count(e => e.EventType == "mission.interrupted_redispatched"), "no further re-dispatch is emitted once the budget is spent");
                }
            });

            await RunTest("HealthCheckAsync waits for a launched runtime to finish draining its output", async () =>
            {
                if (!OperatingSystem.IsLinux()) { SkipTest("HealthCheckAsync waits for a launched runtime to finish draining its output", "Linux process/drain integration"); return; }
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ArmadaSettings settings = CreateSettings();
                settings.MinIdleCaptains = 0;
                AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, settings, new StubGitService());
                using ManualResetEventSlim releaseOutput = new(false);
                TaskCompletionSource<bool> outputHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<int?> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
                global::Test.Shared.Infrastructure.TestAgentRuntime runtime = new(CreateLogging())
                {
                    CommandOverride = "bash",
                    ArgsOverride = new List<string> { "-c", "printf 'held-output\\n'" }
                };
                runtime.OnOutputReceived += (pid, line) =>
                {
                    outputHeld.TrySetResult(true);
                    releaseOutput.Wait(TimeSpan.FromSeconds(20));
                };
                runtime.OnProcessExited += (pid, code) => exited.TrySetResult(code);
                int processId = 0;
                try
                {
                    processId = await runtime.StartAsync(Path.GetTempPath(), "drain ownership test").ConfigureAwait(false);
                    AssertTrue(await Task.WhenAny(outputHeld.Task, Task.Delay(5000)).ConfigureAwait(false) == outputHeld.Task,
                        "the real process output callback is held before exit finalization");
                    DateTime exitDeadline = DateTime.UtcNow.AddSeconds(5);
                    while (ProcessSupervisor.IsTrackedProcessAlive(processId) && DateTime.UtcNow < exitDeadline)
                        await Task.Delay(10).ConfigureAwait(false);
                    AssertFalse(ProcessSupervisor.IsTrackedProcessAlive(processId), "the native process exited while its output is still held");
                    Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission("draining runtime")
                    {
                        Status = MissionStatusEnum.InProgress, AssignmentState = MissionAssignmentStateEnum.Assigned,
                        ProcessId = processId, StartedUtc = DateTime.UtcNow.AddMinutes(-1)
                    }).ConfigureAwait(false);
                    Captain captain = await testDb.Driver.Captains.CreateAsync(new Captain("draining-runtime")
                    {
                        State = CaptainStateEnum.Working, CurrentMissionId = mission.Id, ProcessId = processId
                    }).ConfigureAwait(false);
                    await service.HealthCheckAsync().ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.InProgress, (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!.Status,
                        "health must not complete or fail an owned runtime before its final log writes");
                    releaseOutput.Set();
                    AssertTrue(await Task.WhenAny(exited.Task, Task.Delay(5000)).ConfigureAwait(false) == exited.Task, "drain completes");
                    AssertEqual(0, exited.Task.Result, "healthy effective exit is preserved");
                    await service.HandleProcessExitAsync(processId, exited.Task.Result, captain.Id, mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.WorkProduced, (await testDb.Driver.Missions.ReadAsync(mission.Id).ConfigureAwait(false))!.Status,
                        "normal completion proceeds once finalization is authoritative");
                }
                finally
                {
                    releaseOutput.Set();
                    if (processId > 0)
                    {
                        await Task.WhenAny(exited.Task, Task.Delay(5000)).ConfigureAwait(false);
                        if (!exited.Task.IsCompleted) await runtime.StopAsync(processId).ConfigureAwait(false);
                        ProcessSupervisor.ForgetLaunchedProcess(processId);
                    }
                }
            });

            await RunTest("HealthCheckAsync VanishedProcess IsNotTreatedAsAnInterruption", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.MinIdleCaptains = 0;
                    settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_test_logs_" + Guid.NewGuid().ToString("N"));
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                    Voyage voyage = await db.Voyages.CreateAsync(new Voyage("Vanished process voyage") { Status = VoyageStatusEnum.InProgress }).ConfigureAwait(false);
                    Mission mission = await db.Missions.CreateAsync(new Mission("Vanished process mission")
                    {
                        VoyageId = voyage.Id,
                        Status = MissionStatusEnum.InProgress,
                        AssignmentState = MissionAssignmentStateEnum.Assigned,
                        ProcessId = 99999998,
                        StartedUtc = DateTime.UtcNow.AddMinutes(-5)
                    }).ConfigureAwait(false);
                    Captain captain = new Captain("vanished-process-captain");
                    captain.State = CaptainStateEnum.Working;
                    captain.CurrentMissionId = mission.Id;
                    captain.ProcessId = 99999998;
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    // The health check reports a process it cannot find as -1. That value is not an exit code
                    // the runtime reported: the process may have completed normally after its exit record was
                    // pruned, so re-running the mission could repeat finished work.
                    await service.HealthCheckAsync().ConfigureAwait(false);

                    Mission? updatedMission = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    List<ArmadaEvent> events = await db.Events.EnumerateByMissionAsync(mission.Id, 100).ConfigureAwait(false);

                    AssertFalse(events.Any(e => e.EventType == "mission.interrupted_redispatched"), "a vanished process is never re-dispatched as an interruption");
                    AssertEqual(MissionStatusEnum.Failed, updatedMission!.Status, "a vanished process keeps the loud failure path");
                }
            });

            await RunTest("HandleProcessExitAsync QuarantinesAfterDistinctGenericCrashes", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    settings.CrashLoopDetection.FailureThreshold = 2;
                    settings.CrashLoopDetection.WindowMinutes = 10;
                    settings.CrashLoopDetection.CooldownSeconds = 30;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                    Captain captain = new Captain("crash-loop-captain") { State = CaptainStateEnum.Working };
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                    for (int index = 0; index < 2; index++)
                    {
                        Voyage voyage = new Voyage("Crash loop voyage " + index) { Status = VoyageStatusEnum.InProgress };
                        await db.Voyages.CreateAsync(voyage).ConfigureAwait(false);
                        Mission mission = new Mission("Generic runtime crash " + index)
                        {
                            VoyageId = voyage.Id,
                            Status = MissionStatusEnum.InProgress,
                            AssignmentState = MissionAssignmentStateEnum.Assigned,
                            ProcessId = 9100 + index,
                            StartedUtc = DateTime.UtcNow.AddSeconds(-5)
                        };
                        await db.Missions.CreateAsync(mission).ConfigureAwait(false);
                        captain.CurrentMissionId = mission.Id;
                        captain.ProcessId = mission.ProcessId;
                        captain.State = CaptainStateEnum.Working;
                        await db.Captains.UpdateAsync(captain).ConfigureAwait(false);

                        await service.HandleProcessExitAsync(mission.ProcessId!.Value, 139, captain.Id, mission.Id).ConfigureAwait(false);
                        if (index == 0)
                        {
                            // The lifecycle callback can race a health observation. A duplicate
                            // callback for the same process must not create a second crash sample.
                            await service.HandleProcessExitAsync(mission.ProcessId!.Value, 139, captain.Id, mission.Id).ConfigureAwait(false);
                            Captain? duplicateAfter = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                            AssertNotEqual(CaptainStateEnum.Quarantined, duplicateAfter!.State, "A duplicate callback must not reach the crash threshold.");
                        }
                        captain = (await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                    }

                    Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertEqual(CaptainStateEnum.Quarantined, after!.State, "Repeated generic runtime crashes must quarantine the captain.");
                    AssertContains("Crash loop detected", after.QuarantineReason ?? String.Empty, "Quarantine must identify crash-loop protection.");
                    AssertTrue(after.QuarantineUntilUtc.HasValue, "Crash-loop quarantine must have a bounded cooldown.");
                }
            });

            await RunTest("HandleProcessExitAsync ExcludesProviderFailuresBeforeGenericCrashFromCrashLoop", async () =>
            {
                CrashLoopExcludedFailureCase[] cases = new CrashLoopExcludedFailureCase[]
                {
                    new CrashLoopExcludedFailureCase(
                        "quota",
                        "[stderr] You've hit your usage limit. try again at 11:57 PM\nAgent exited with code 1",
                        benchesCaptain: true),
                    new CrashLoopExcludedFailureCase(
                        "auth",
                        "[stderr] invalid_api_key\nAgent exited with code 1",
                        benchesCaptain: true),
                    new CrashLoopExcludedFailureCase(
                        "throttle",
                        "[stderr] stream error: exceeded retry limit, last status: 429 Too Many Requests\nAgent exited with code 1",
                        benchesCaptain: true),
                    new CrashLoopExcludedFailureCase(
                        "overload",
                        "[stderr] API Error: 529 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\nAgent exited with code 1",
                        benchesCaptain: true),
                    new CrashLoopExcludedFailureCase(
                        "safeguard",
                        "[stderr] Safety measures that flagged this message for a cybersecurity topic\nAgent exited with code 1",
                        benchesCaptain: false)
                };

                foreach (CrashLoopExcludedFailureCase testCase in cases)
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        SqliteDatabaseDriver db = testDb.Driver;
                        ArmadaSettings settings = CreateSettings();
                        settings.CrashLoopDetection.FailureThreshold = 2;
                        settings.CrashLoopDetection.WindowMinutes = 10;
                        settings.CrashLoopDetection.CooldownSeconds = 30;
                        AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                        Captain captain = new Captain("excluded-provider-" + testCase.Name)
                        {
                            State = CaptainStateEnum.Idle
                        };
                        await db.Captains.CreateAsync(captain).ConfigureAwait(false);

                        Mission providerFailure = await CreateExitMissionAsync(db, captain, 9500).ConfigureAwait(false);
                        string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                        Directory.CreateDirectory(missionLogDir);
                        await File.WriteAllTextAsync(
                            Path.Combine(missionLogDir, providerFailure.Id + ".log"),
                            testCase.LogText).ConfigureAwait(false);

                        await service.HandleProcessExitAsync(
                            providerFailure.ProcessId!.Value,
                            1,
                            captain.Id,
                            providerFailure.Id).ConfigureAwait(false);

                        Captain? providerAfter = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                        if (testCase.BenchesCaptain)
                        {
                            AssertEqual(
                                CaptainStateEnum.Quarantined,
                                providerAfter!.State,
                                testCase.Name + " provider failure must use the quarantine path before reassignment.");
                        }
                        else
                        {
                            // With no captain on another runtime, the refusal rule stops the mission with the reason
                            // and releases the captain instead of benching it.
                            AssertNotEqual(
                                CaptainStateEnum.Quarantined,
                                providerAfter!.State,
                                testCase.Name + " provider failure must not bench the captain.");
                            Mission? blocked = await db.Missions.ReadAsync(providerFailure.Id).ConfigureAwait(false);
                            AssertTrue(
                                (blocked!.FailureReason ?? "").StartsWith(PolicyRefusalContinuationService.StoppedReasonPrefix, StringComparison.Ordinal),
                                testCase.Name + " provider failure must stop with the refusal reason: " + blocked.FailureReason);
                        }

                        Mission genericCrash = await CreateExitMissionAsync(db, captain, 9501).ConfigureAwait(false);
                        await service.HandleProcessExitAsync(
                            genericCrash.ProcessId!.Value,
                            139,
                            captain.Id,
                            genericCrash.Id).ConfigureAwait(false);

                        Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                        AssertNotEqual(
                            CaptainStateEnum.Quarantined,
                            after!.State,
                            testCase.Name + " provider failure must not contribute to the generic crash-loop threshold.");
                    }
                }
            });

            await RunTest("IsModelGatewayTimeoutFailureReason matches only a gateway status that gave up waiting", () =>
            {
                AssertTrue(AdmiralService.IsModelGatewayTimeoutFailureReason(
                    "[ARMADA:ACTIVITY] mux error LLM request to http://model.example/v1 failed with status 502: HTTP 502: {\"error\":\"Proxy error: The operation was canceled.\"}"),
                    "A 502 whose proxy canceled the request is a gateway timeout.");
                AssertTrue(AdmiralService.IsModelGatewayTimeoutFailureReason("request failed with status 504: Gateway Timeout"),
                    "A 504 gateway timeout is a gateway timeout.");
                AssertFalse(AdmiralService.IsModelGatewayTimeoutFailureReason("request failed with status 502: Bad Gateway"),
                    "A 502 that does not say it timed out is not retried.");
                AssertFalse(AdmiralService.IsModelGatewayTimeoutFailureReason("tool call timed out after 30s"),
                    "A timeout without a gateway status is not retried.");
                AssertFalse(AdmiralService.IsModelGatewayTimeoutFailureReason(null), "No reason is not a gateway timeout.");
                return Task.CompletedTask;
            });

            // A self-hosted model can take longer to load cold than its gateway waits. The first such failure
            // re-runs the mission; a second is a real outage and fails it.
            await RunTest("HandleProcessExitAsync re-runs a mission once after a model gateway timeout", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    SqliteDatabaseDriver db = testDb.Driver;
                    ArmadaSettings settings = CreateSettings();
                    AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                    Captain captain = new Captain("gateway-timeout")
                    {
                        State = CaptainStateEnum.Idle,
                        Runtime = AgentRuntimeEnum.Mux,
                        RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new MuxCaptainOptions { Endpoint = "gateway-primary" })
                    };
                    await db.Captains.CreateAsync(captain).ConfigureAwait(false);
                    Mission mission = await CreateExitMissionAsync(db, captain, 9600).ConfigureAwait(false);
                    string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                    Directory.CreateDirectory(missionLogDir);
                    await File.WriteAllTextAsync(
                        Path.Combine(missionLogDir, mission.Id + ".log"),
                        "[ARMADA:ACTIVITY] mux starting\n"
                        + "[ARMADA:ACTIVITY] mux error LLM request to http://model.example/v1 failed with status 502: HTTP 502: {\"error\":\"Proxy error: The operation was canceled.\"}\n").ConfigureAwait(false);

                    await service.HandleProcessExitAsync(9600, 1, captain.Id, mission.Id).ConfigureAwait(false);

                    Mission? requeued = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, requeued!.Status, "The first gateway timeout must re-run the mission.");
                    EnumerationResult<ArmadaEvent> retries = await db.Events.EnumerateAsync(new EnumerationQuery
                    {
                        MissionId = mission.Id,
                        EventType = AdmiralService.ModelGatewayRetryEvent
                    }).ConfigureAwait(false);
                    AssertEqual(1L, retries.TotalRecords, "The re-run must be recorded once.");

                    Captain again = await db.Captains.CreateAsync(new Captain("gateway-timeout-alternate")
                    {
                        Runtime = AgentRuntimeEnum.Mux,
                        RuntimeOptionsJson = captain.RuntimeOptionsJson
                    }).ConfigureAwait(false);
                    requeued.Status = MissionStatusEnum.InProgress;
                    requeued.AssignmentState = MissionAssignmentStateEnum.Assigned;
                    requeued.CaptainId = again!.Id;
                    requeued.ProcessId = 9601;
                    await db.Missions.UpdateAsync(requeued).ConfigureAwait(false);
                    again.CurrentMissionId = mission.Id;
                    again.ProcessId = 9601;
                    again.State = CaptainStateEnum.Working;
                    await db.Captains.UpdateAsync(again).ConfigureAwait(false);

                    await service.HandleProcessExitAsync(9601, 1, again.Id, mission.Id).ConfigureAwait(false);

                    Mission? failed = await db.Missions.ReadAsync(mission.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Failed, failed!.Status, "A second gateway timeout must fail the mission even on another captain at the same endpoint.");
                    AssertContains("provider_timeout", failed.FailureReason ?? String.Empty, "The terminal failure names its class.");
                    AssertContains("endpoint=gateway-primary", failed.FailureReason ?? String.Empty, "The terminal failure names the configured endpoint.");
                    ProductionFactPage<MissionAttemptFact> facts = await db.MissionAttemptFacts.EnumerateAsync(new ProductionFactQuery
                    {
                        FromUtc = DateTime.UtcNow.AddMinutes(-5),
                        ToUtc = DateTime.UtcNow.AddMinutes(1)
                    }).ConfigureAwait(false);
                    MissionAttemptFact? timeoutFact = facts.Items.FirstOrDefault(fact => fact.MissionId == mission.Id && fact.FactType == MissionAttemptFactTypeEnum.Failed);
                    AssertNotNull(timeoutFact, "The terminal timeout stores a failure fact.");
                    AssertEqual("provider_timeout", timeoutFact!.ReasonCode, "The typed timeout decision retains its stable cause.");
                    retries = await db.Events.EnumerateAsync(new EnumerationQuery
                    {
                        MissionId = mission.Id,
                        EventType = AdmiralService.ModelGatewayRetryEvent
                    }).ConfigureAwait(false);
                    AssertEqual(1L, retries.TotalRecords, "The second timeout must not spend another re-run.");
                }
            });

            // A crash whose output names a pool capacity, a billing module, a line number or a process id is
            // still a crash: only a provider phrase or a status form reads as a provider limit.
            await RunTest("HandleProcessExitAsync QuarantinesACrashLoopWhoseOutputMerelyMentionsCapacityOrBilling", async () =>
            {
                string[] crashLogs =
                {
                    "Unhandled exception. System.InvalidOperationException: Pool capacity exceeded\n   at Worker.Run() in Worker.cs:line 429\nAgent exited with code 134",
                    "Unhandled exception. System.NullReferenceException in BillingTests.Setup (pid 14290)\nAgent exited with code 134"
                };

                foreach (string crashLog in crashLogs)
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        SqliteDatabaseDriver db = testDb.Driver;
                        ArmadaSettings settings = CreateSettings();
                        settings.CrashLoopDetection.FailureThreshold = 2;
                        settings.CrashLoopDetection.WindowMinutes = 10;
                        settings.CrashLoopDetection.CooldownSeconds = 30;
                        AdmiralService service = CreateAdmiralService(CreateLogging(), db, settings, new StubGitService());

                        Captain captain = new Captain("capacity-crash-loop") { State = CaptainStateEnum.Idle };
                        await db.Captains.CreateAsync(captain).ConfigureAwait(false);
                        string missionLogDir = Path.Combine(settings.LogDirectory, "missions");
                        Directory.CreateDirectory(missionLogDir);

                        for (int index = 0; index < 2; index++)
                        {
                            Mission mission = await CreateExitMissionAsync(db, captain, 9600 + index).ConfigureAwait(false);
                            await File.WriteAllTextAsync(Path.Combine(missionLogDir, mission.Id + ".log"), crashLog).ConfigureAwait(false);
                            await service.HandleProcessExitAsync(mission.ProcessId!.Value, 134, captain.Id, mission.Id).ConfigureAwait(false);
                            captain = (await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                        }

                        Captain? after = await db.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                        AssertEqual(CaptainStateEnum.Quarantined, after!.State, "A repeated crash must reach crash-loop quarantine. Log: " + crashLog);
                        AssertContains("Crash loop detected", after.QuarantineReason ?? String.Empty, "The hold must be the crash-loop hold, not a provider bench. Log: " + crashLog);
                    }
                }
            });

            await RunTest("HandleProcessExitAsync DisabledCrashLoopDetectionDoesNotQuarantine", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    settings.CrashLoopDetection.Enabled = false;
                    settings.CrashLoopDetection.FailureThreshold = 2;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, settings, new StubGitService());
                    Captain captain = new Captain("disabled-crash-loop") { State = CaptainStateEnum.Idle };
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    for (int index = 0; index < 3; index++)
                    {
                        Mission mission = await CreateExitMissionAsync(testDb.Driver, captain, 9200 + index).ConfigureAwait(false);
                        await service.HandleProcessExitAsync(mission.ProcessId!.Value, 139, captain.Id, mission.Id).ConfigureAwait(false);
                        captain = (await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                    }
                    Captain? after = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotEqual(CaptainStateEnum.Quarantined, after!.State, "Disabled crash-loop detection must never quarantine.");
                }
            });

            await RunTest("HandleProcessExitAsync OomAndInterruptionDoNotCountAsCrashLoop", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    ArmadaSettings settings = CreateSettings();
                    settings.CrashLoopDetection.FailureThreshold = 2;
                    AdmiralService service = CreateAdmiralService(CreateLogging(), testDb.Driver, settings, new StubGitService());
                    Captain captain = new Captain("excluded-crash-loop") { State = CaptainStateEnum.Idle };
                    await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);
                    Mission oom = await CreateExitMissionAsync(testDb.Driver, captain, 9300).ConfigureAwait(false);
                    await service.HandleProcessExitAsync(9300, 137, captain.Id, oom.Id).ConfigureAwait(false);
                    captain = (await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                    Mission interrupted = await CreateExitMissionAsync(testDb.Driver, captain, 9301).ConfigureAwait(false);
                    try
                    {
                        await service.HandleProcessExitAsync(9301, -1, captain.Id, interrupted.Id, new CancellationToken(true)).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation before failure handling must not create crash-loop evidence.
                    }
                    captain = (await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false))!;
                    Mission generic = await CreateExitMissionAsync(testDb.Driver, captain, 9302).ConfigureAwait(false);
                    await service.HandleProcessExitAsync(9302, 139, captain.Id, generic.Id).ConfigureAwait(false);
                    Captain? after = await testDb.Driver.Captains.ReadAsync(captain.Id).ConfigureAwait(false);
                    AssertNotEqual(CaptainStateEnum.Quarantined, after!.State, "OOM and interruption must not contribute to crash-loop threshold.");
                }
            });
        }
    }
}
