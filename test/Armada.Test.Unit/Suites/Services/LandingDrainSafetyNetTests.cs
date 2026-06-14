namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;

    /// <summary>
    /// Regression tests for the landing-drain safety-net sweep in AutonomousRecoveryOrchestrator.
    /// </summary>
    public class LandingDrainSafetyNetTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Landing Drain Safety Net";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("SweepAsync_JudgePassedWorkProduced_EnqueuesBranch", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-1", "Worker").ConfigureAwait(false);
                Mission judge = await CreateCompleteJudgeAsync(testDb, vessel, voyage, worker.Id, pass: true).ConfigureAwait(false);

                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, mergeQueue.EnqueueCalls.Count, "Expected one safety-net enqueue.");
                AssertEqual(worker.Id, mergeQueue.EnqueueCalls[0].MissionId, "Enqueue should target the Worker mission.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_AlreadyEnqueued_DoesNotDoubleEnqueue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-2", "Worker").ConfigureAwait(false);
                await CreateCompleteJudgeAsync(testDb, vessel, voyage, worker.Id, pass: true).ConfigureAwait(false);

                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                mergeQueue.ActiveMissionIds.Add(worker.Id);
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, mergeQueue.EnqueueCalls.Count, "Active merge entry should prevent a second enqueue.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_PredicateFail_FlagsForReview", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                vessel.AutoLandPredicate = "{\"enabled\":true,\"maxFiles\":1}";
                await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);

                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-3", "Worker").ConfigureAwait(false);
                await CreateCompleteJudgeAsync(testDb, vessel, voyage, worker.Id, pass: true).ConfigureAwait(false);

                RecordingMergeQueueService mergeQueue = new RecordingMergeQueueService();
                StubGitService git = new StubGitService
                {
                    DiffResult = "+++ b/src/A.cs\n+line1\n+++ b/src/B.cs\n+line2\n"
                };
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, mergeQueue, git: git);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, mergeQueue.EnqueueCalls.Count);
                AssertEqual(SafetyNetEnqueueOutcomeEnum.EnqueuedFlaggedForReview, mergeQueue.LastOutcome, "Over-cap diff should flag for review.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_FailedLeafJudge_DispatchesRescue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);
                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/worker-4", "Worker").ConfigureAwait(false);

                Mission judge = new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    DependsOnMissionId = worker.Id,
                    Persona = "Judge",
                    Title = "Judge stage",
                    Status = MissionStatusEnum.Failed,
                    FailureReason = "Judge verdict: NEEDS_REVISION",
                    ReviewComment = "Add regression coverage.",
                    CompletedUtc = DateTime.UtcNow.AddMinutes(-5),
                    LastUpdateUtc = DateTime.UtcNow.AddMinutes(-5)
                };
                await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, new RecordingMergeQueueService(), admiral);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchedMissions.Count, "Failed leaf judge should dispatch a Worker rescue.");
                AssertEqual("Worker", admiral.DispatchedMissions[0].Persona);
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_AllTerminal_CompletesOpenVoyage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await CreateOpenVoyageAsync(testDb).ConfigureAwait(false);

                await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    Persona = "Worker",
                    Title = "Done worker",
                    Status = MissionStatusEnum.Complete,
                    CompletedUtc = DateTime.UtcNow.AddHours(-1),
                    LastUpdateUtc = DateTime.UtcNow.AddHours(-1)
                }).ConfigureAwait(false);

                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(testDb.Driver, new RecordingMergeQueueService());

                await orchestrator.SweepAsync().ConfigureAwait(false);

                Voyage? updated = await testDb.Driver.Voyages.ReadAsync(voyage.Id).ConfigureAwait(false);
                AssertNotNull(updated);
                AssertEqual(VoyageStatusEnum.Complete, updated!.Status, "Idle voyage with only terminal missions should complete.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_StuckOpenVoyage_OpensIncident", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                await EnsureTenantAndUserAsync(testDb).ConfigureAwait(false);

                Vessel vessel = await CreateVesselAsync(testDb).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Stuck voyage", "No progress")
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    Status = VoyageStatusEnum.Open,
                    LastUpdateUtc = DateTime.UtcNow.AddHours(-2)
                }).ConfigureAwait(false);

                Mission worker = await CreateWorkProducedMissionAsync(testDb, vessel, voyage, "armada/stuck-worker", "Worker").ConfigureAwait(false);

                Mission judge = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    TenantId = vessel.TenantId,
                    UserId = vessel.UserId,
                    VesselId = vessel.Id,
                    VoyageId = voyage.Id,
                    DependsOnMissionId = worker.Id,
                    Persona = "Judge",
                    Title = "Stalled judge",
                    Status = MissionStatusEnum.Pending
                }).ConfigureAwait(false);

                DateTime staleUtc = DateTime.UtcNow.AddHours(-2);
                await SetMissionLastUpdateUtcAsync(testDb, worker.Id, staleUtc).ConfigureAwait(false);
                await SetMissionLastUpdateUtcAsync(testDb, judge.Id, staleUtc).ConfigureAwait(false);
                await SetVoyageLastUpdateUtcAsync(testDb, voyage.Id, staleUtc).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings();
                settings.AutonomousRecovery.StuckOpenVoyageMinutes = 30;

                IncidentService incidents = new IncidentService(testDb.Driver);
                AutonomousRecoveryOrchestrator orchestrator = CreateDrainOrchestrator(
                    testDb.Driver,
                    new RecordingMergeQueueService(),
                    settings: settings,
                    incidents: incidents);

                await orchestrator.SweepAsync().ConfigureAwait(false);

                AuthContext auth = AuthContext.Authenticated(vessel.TenantId!, vessel.UserId!, false, true, "UnitTest");
                EnumerationResult<Incident> page = await incidents.EnumerateAsync(auth, new IncidentQuery
                {
                    VoyageId = voyage.Id,
                    PageNumber = 1,
                    PageSize = 10
                }).ConfigureAwait(false);

                AssertEqual(1, page.Objects.Count, "Stuck open voyage should open an incident.");
                AssertContains("no live missions", page.Objects[0].Summary ?? "", "Incident summary should describe the stall.");
            }).ConfigureAwait(false);
        }

        #region Helpers

        private static async Task SetMissionLastUpdateUtcAsync(TestDatabase testDb, string missionId, DateTime lastUpdateUtc)
        {
            using (SqliteConnection conn = new SqliteConnection(testDb.ConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE missions SET last_update_utc = @last_update_utc WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", missionId);
                    cmd.Parameters.AddWithValue(
                        "@last_update_utc",
                        lastUpdateUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        private static async Task SetVoyageLastUpdateUtcAsync(TestDatabase testDb, string voyageId, DateTime lastUpdateUtc)
        {
            using (SqliteConnection conn = new SqliteConnection(testDb.ConnectionString))
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE voyages SET last_update_utc = @last_update_utc WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", voyageId);
                    cmd.Parameters.AddWithValue(
                        "@last_update_utc",
                        lastUpdateUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture));
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
        }

        private sealed class RecordingMergeQueueService : IMergeQueueService
        {
            public List<MergeEntry> EnqueueCalls { get; } = new List<MergeEntry>();
            public HashSet<string> ActiveMissionIds { get; } = new HashSet<string>(StringComparer.Ordinal);
            public SafetyNetEnqueueOutcomeEnum LastOutcome { get; private set; }

            public Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default)
            {
                EnqueueCalls.Add(entry);
                return Task.FromResult(entry);
            }

            public Task<bool> HasActiveMergeEntryForMissionAsync(string missionId, CancellationToken token = default)
            {
                return Task.FromResult(ActiveMissionIds.Contains(missionId));
            }

            public Task<SafetyNetEnqueueResult> TrySafetyNetEnqueueAsync(
                Mission mission,
                Vessel vessel,
                string? unifiedDiff,
                IAutoLandEvaluator autoLandEvaluator,
                IConventionChecker conventionChecker,
                ICriticalTriggerEvaluator criticalTriggerEvaluator,
                CancellationToken token = default)
            {
                if (HasActiveMergeEntryForMissionAsync(mission.Id, token).Result)
                {
                    return Task.FromResult(new SafetyNetEnqueueResult(SafetyNetEnqueueOutcomeEnum.AlreadyEnqueued, null));
                }

                MergeEntry entry = new MergeEntry(mission.BranchName ?? "branch", vessel.DefaultBranch ?? "main");
                entry.MissionId = mission.Id;
                entry.VesselId = vessel.Id;
                entry.Status = MergeStatusEnum.Queued;

                AutoLandPredicate? predicate = vessel.GetAutoLandPredicate();
                SafetyNetEnqueueOutcomeEnum outcome = SafetyNetEnqueueOutcomeEnum.Enqueued;
                string? detail = null;
                if (predicate is { Enabled: true })
                {
                    EvaluationResult result = autoLandEvaluator.Evaluate(unifiedDiff ?? String.Empty, predicate);
                    if (result is EvaluationResult.Fail fail)
                    {
                        outcome = SafetyNetEnqueueOutcomeEnum.EnqueuedFlaggedForReview;
                        detail = fail.Reason;
                        entry.AuditDeepPicked = true;
                    }
                }

                LastOutcome = outcome;
                EnqueueCalls.Add(entry);
                return Task.FromResult(new SafetyNetEnqueueResult(outcome, entry, detail));
            }

            public Task ProcessQueueAsync(CancellationToken token = default) => Task.CompletedTask;
            public Task CancelAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.CompletedTask;
            public Task<List<MergeEntry>> ListAsync(string? tenantId = null, CancellationToken token = default) => Task.FromResult(new List<MergeEntry>());
            public Task<MergeEntry?> ProcessSingleAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task ProcessEntryByIdAsync(string entryId, CancellationToken token = default) => Task.CompletedTask;
            public Task<MergeEntry?> GetAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task<bool> DeleteAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult(false);
            public Task<MergeQueuePurgeResult> DeleteMultipleAsync(List<string> entryIds, string? tenantId = null, CancellationToken token = default) => Task.FromResult(new MergeQueuePurgeResult());
            public Task<int> PurgeTerminalAsync(string? vesselId = null, MergeStatusEnum? status = null, string? tenantId = null, CancellationToken token = default) => Task.FromResult(0);
            public Task<int> ReconcilePullRequestEntriesAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> ReconcileLandingStateMachineAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> RecoverInFlightLandingsAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<bool> TryOpenPullRequestForRecoveryAsync(string mergeEntryId, CancellationToken token = default) => Task.FromResult(false);
        }

        private static AutonomousRecoveryOrchestrator CreateDrainOrchestrator(
            DatabaseDriver database,
            RecordingMergeQueueService mergeQueue,
            RecordingAdmiralService? admiral = null,
            ArmadaSettings? settings = null,
            IncidentService? incidents = null,
            StubGitService? git = null)
        {
            RecordingAdmiralService admiralService = admiral ?? new RecordingAdmiralService(database);
            IncidentService incidentService = incidents ?? new IncidentService(database);
            RunbookService runbooks = new RunbookService(database, new LoggingModule());
            StubGitService gitService = git ?? new StubGitService { DiffResult = "+++ b/src/A.cs\n+line1\n" };

            return new AutonomousRecoveryOrchestrator(
                database,
                admiralService,
                incidentService,
                runbooks,
                settings ?? new ArmadaSettings(),
                new LoggingModule(),
                mergeQueue,
                gitService,
                new AutoLandEvaluator(),
                new ConventionChecker(),
                new CriticalTriggerEvaluator());
        }

        private static async Task EnsureTenantAndUserAsync(TestDatabase testDb)
        {
            TenantMetadata? existingTenant = await testDb.Driver.Tenants.ReadAsync("ten_drain").ConfigureAwait(false);
            if (existingTenant == null)
            {
                await testDb.Driver.Tenants.CreateAsync(new TenantMetadata
                {
                    Id = "ten_drain",
                    Name = "ten_drain"
                }).ConfigureAwait(false);
            }

            UserMaster? existingUser = await testDb.Driver.Users.ReadByIdAsync("usr_drain").ConfigureAwait(false);
            if (existingUser == null)
            {
                await testDb.Driver.Users.CreateAsync(new UserMaster
                {
                    Id = "usr_drain",
                    TenantId = "ten_drain",
                    Email = "usr_drain@armada.test",
                    PasswordSha256 = UserMaster.ComputePasswordHash("password"),
                    IsTenantAdmin = true
                }).ConfigureAwait(false);
            }
        }

        private static async Task<Vessel> CreateVesselAsync(TestDatabase testDb)
        {
            Vessel vessel = new Vessel
            {
                TenantId = "ten_drain",
                UserId = "usr_drain",
                Name = "Drain Vessel",
                RepoUrl = "file:///tmp/drain.git",
                LocalPath = "C:\\tmp\\drain",
                WorkingDirectory = "C:\\tmp\\drain",
                DefaultBranch = "main"
            };
            return await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);
        }

        private static async Task<Voyage> CreateOpenVoyageAsync(TestDatabase testDb)
        {
            return await testDb.Driver.Voyages.CreateAsync(new Voyage("Drain voyage", "Landing drain test")
            {
                TenantId = "ten_drain",
                UserId = "usr_drain",
                Status = VoyageStatusEnum.Open,
                LastUpdateUtc = DateTime.UtcNow
            }).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateWorkProducedMissionAsync(
            TestDatabase testDb,
            Vessel vessel,
            Voyage voyage,
            string branchName,
            string persona)
        {
            Mission mission = new Mission
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                VesselId = vessel.Id,
                VoyageId = voyage.Id,
                Persona = persona,
                Title = persona + " mission",
                BranchName = branchName,
                Status = MissionStatusEnum.WorkProduced,
                LastUpdateUtc = DateTime.UtcNow
            };
            return await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateCompleteJudgeAsync(
            TestDatabase testDb,
            Vessel vessel,
            Voyage voyage,
            string dependsOnMissionId,
            bool pass)
        {
            string output = pass
                ? "## Completeness\nok\n## Correctness\nok\n## Tests\nok\n## Failure Modes\nok\n## Verdict\nPASS\n[ARMADA:VERDICT] PASS"
                : "## Verdict\nFAIL\n[ARMADA:VERDICT] FAIL";

            Mission judge = new Mission
            {
                TenantId = vessel.TenantId,
                UserId = vessel.UserId,
                VesselId = vessel.Id,
                VoyageId = voyage.Id,
                DependsOnMissionId = dependsOnMissionId,
                Persona = "Judge",
                Title = "Judge mission",
                Status = MissionStatusEnum.Complete,
                AgentOutput = output,
                CompletedUtc = DateTime.UtcNow,
                LastUpdateUtc = DateTime.UtcNow
            };
            return await testDb.Driver.Missions.CreateAsync(judge).ConfigureAwait(false);
        }

        private sealed class RecordingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public RecordingAdmiralService(DatabaseDriver database)
            {
                _Database = database;
            }

            public List<Mission> DispatchedMissions { get; } = new List<Mission>();

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            public async Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
            {
                Mission created = await _Database.Missions.CreateAsync(mission, token).ConfigureAwait(false);
                DispatchedMissions.Add(created);
                return created;
            }

            public Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default)
                => Task.FromResult<Pipeline?>(null);

            public Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default)
                => Task.FromResult(new ArmadaStatus());

            public Task RecallCaptainAsync(string captainId, CancellationToken token = default)
                => Task.CompletedTask;

            public Task RecallAllAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task HealthCheckAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => Task.CompletedTask;
        }

        #endregion
    }
}
