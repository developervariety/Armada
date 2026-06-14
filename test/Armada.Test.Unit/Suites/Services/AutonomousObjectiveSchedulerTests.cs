namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
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
    /// Unit coverage for AutonomousObjectiveScheduler sweep gating added when wiring
    /// the scheduler into the ArmadaServer health loop.
    /// </summary>
    public class AutonomousObjectiveSchedulerTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Autonomous Objective Scheduler";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("SweepAsync_SecondImmediateCallWithinInterval_IsNoOp", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        IntervalMinutes = 15
                    }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);

                await scheduler.SweepAsync().ConfigureAwait(false);

                DateTime? firstTick = scheduler.LastTickUtc;
                string? firstSummary = scheduler.LastResultSummary;
                int dispatchCountAfterFirst = admiral.DispatchVoyageCallCount;

                AssertTrue(firstTick.HasValue, "First sweep should record LastTickUtc.");
                AssertTrue(!String.IsNullOrWhiteSpace(firstSummary), "First sweep should record LastResultSummary.");

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(firstTick, scheduler.LastTickUtc, "Second sweep within interval must not advance LastTickUtc.");
                AssertEqual(firstSummary, scheduler.LastResultSummary, "Second sweep within interval must not change LastResultSummary.");
                AssertEqual(dispatchCountAfterFirst, admiral.DispatchVoyageCallCount, "Second sweep within interval must not dispatch voyages.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_Disabled_EmitsSkippedDisabledEvent", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    new ArmadaSettings());

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual("skipped (disabled)", scheduler.LastResultSummary, "Disabled scheduler should record skip summary.");

                List<ArmadaEvent> skippedEvents = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_disabled")
                    .ConfigureAwait(false);
                AssertEqual(1, skippedEvents.Count, "Disabled sweep should emit exactly one skipped_disabled event.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_Paused_EmitsSkippedPausedEvent", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        Paused = true
                    }
                };

                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    settings);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual("skipped (paused)", scheduler.LastResultSummary, "Paused scheduler should record skip summary.");

                List<ArmadaEvent> pausedEvents = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_paused")
                    .ConfigureAwait(false);
                AssertEqual(1, pausedEvents.Count, "Paused sweep should emit exactly one skipped_paused event.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_DisabledSecondImmediateCallWithinInterval_DoesNotEmitDuplicateEvent", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    new ArmadaSettings());

                await scheduler.SweepAsync().ConfigureAwait(false);
                await scheduler.SweepAsync().ConfigureAwait(false);

                List<ArmadaEvent> skippedEvents = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_disabled")
                    .ConfigureAwait(false);
                AssertEqual(1, skippedEvents.Count, "Interval guard must prevent a second disabled sweep from emitting another event.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_ObjectiveWithActiveLinkedVoyage_DoesNotDispatchDuplicate", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("dup-guard-vessel", "https://github.com/test/dup.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);

                Voyage activeVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Active voyage")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);

                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Already dispatched",
                    Status = ObjectiveStatusEnum.Planned,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { activeVoyage.Id }
                }).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        IntervalMinutes = 1
                    }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchVoyageCallCount, "Scheduler must not dispatch when linked voyage ids already exist.");
                AssertContains("dispatched=0", scheduler.LastResultSummary ?? string.Empty, "Sweep summary should show zero dispatches.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_AfterFirstDispatch_SecondSchedulerInstanceDoesNotDispatchAgain", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("repeat-guard-vessel", "https://github.com/test/repeat.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);

                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "First dispatch only",
                    Status = ObjectiveStatusEnum.Scoped,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id }
                }).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        IntervalMinutes = 1,
                        MaxConcurrentVoyages = 5
                    }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler firstScheduler = CreateScheduler(testDb.Driver, admiral, settings);
                await firstScheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchVoyageCallCount, "First sweep should dispatch exactly one voyage.");

                AutonomousObjectiveScheduler secondScheduler = CreateScheduler(testDb.Driver, admiral, settings);
                await secondScheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchVoyageCallCount, "Second sweep must not create a duplicate voyage for the same objective.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_CompletedObjective_DoesNotRedispatch", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("completed-guard-vessel", "https://github.com/test/completed.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);

                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Completed objective",
                    Status = ObjectiveStatusEnum.Completed,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id }
                }).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        IntervalMinutes = 1
                    }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchVoyageCallCount, "Scheduler must never re-dispatch a Completed objective.");
                AssertContains("dispatched=0", scheduler.LastResultSummary ?? string.Empty, "Sweep summary should show zero dispatches.");
                AssertNotNull(objective.Id, "Objective fixture should have an id.");
            }).ConfigureAwait(false);
        }

        private static AutonomousObjectiveScheduler CreateScheduler(
            DatabaseDriver database,
            IAdmiralService admiral,
            ArmadaSettings settings)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            return new AutonomousObjectiveScheduler(
                database,
                new ObjectiveService(database),
                admiral,
                new StubMergeQueueService(),
                settings,
                logging);
        }

        private sealed class StubMergeQueueService : IMergeQueueService
        {
            public Task<MergeEntry> EnqueueAsync(MergeEntry entry, CancellationToken token = default) => Task.FromResult(entry);
            public Task ProcessQueueAsync(CancellationToken token = default) => Task.CompletedTask;
            public Task CancelAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.CompletedTask;
            public Task<List<MergeEntry>> ListAsync(string? tenantId = null, CancellationToken token = default) => Task.FromResult(new List<MergeEntry>());
            public Task<MergeEntry?> ProcessSingleAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task ProcessEntryByIdAsync(string entryId, CancellationToken token = default) => Task.CompletedTask;
            public Task<MergeEntry?> GetAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult<MergeEntry?>(null);
            public Task<bool> DeleteAsync(string entryId, string? tenantId = null, CancellationToken token = default) => Task.FromResult(false);
            public Task<MergeQueuePurgeResult> DeleteMultipleAsync(List<string> entryIds, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult(new MergeQueuePurgeResult());
            public Task<int> PurgeTerminalAsync(string? vesselId = null, MergeStatusEnum? status = null, string? tenantId = null, CancellationToken token = default)
                => Task.FromResult(0);
            public Task<int> ReconcilePullRequestEntriesAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> ReconcileLandingStateMachineAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<int> RecoverInFlightLandingsAsync(CancellationToken token = default) => Task.FromResult(0);
            public Task<bool> TryOpenPullRequestForRecoveryAsync(string mergeEntryId, CancellationToken token = default) => Task.FromResult(false);
            public Task<bool> HasActiveMergeEntryForMissionAsync(string missionId, CancellationToken token = default) => Task.FromResult(false);
            public Task<SafetyNetEnqueueResult> TrySafetyNetEnqueueAsync(Mission mission, Vessel vessel, string? unifiedDiff, IAutoLandEvaluator autoLandEvaluator, IConventionChecker conventionChecker, ICriticalTriggerEvaluator criticalTriggerEvaluator, CancellationToken token = default)
                => Task.FromResult(new SafetyNetEnqueueResult(SafetyNetEnqueueOutcomeEnum.Enqueued, null));
        }

        private sealed class RecordingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public RecordingAdmiralService(DatabaseDriver database)
            {
                _Database = database;
            }

            public int DispatchVoyageCallCount { get; private set; }

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

            public async Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
            {
                DispatchVoyageCallCount++;
                Voyage voyage = new Voyage
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = title,
                    Description = description,
                    Status = VoyageStatusEnum.InProgress
                };
                return await _Database.Voyages.CreateAsync(voyage, token).ConfigureAwait(false);
            }

            public Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default)
                => throw new NotImplementedException();

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
    }
}
