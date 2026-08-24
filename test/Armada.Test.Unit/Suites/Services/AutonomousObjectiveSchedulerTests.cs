namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
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

            await RunTest("An operator-dispatched voyage counts toward the concurrency limit", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                    new Vessel("concurrency-vessel", "https://github.com/test/conc.git")
                    {
                        TenantId = Constants.DefaultTenantId
                    }).ConfigureAwait(false);

                // Armada cannot tell an operator's voyage from its own once both are linked to an
                // objective, and it should not try: a second autonomous voyage against work a human
                // is already doing duplicates it rather than adding throughput. The limit therefore
                // gates what the SCHEDULER starts, and the count can exceed it.
                Voyage operatorVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("operator-dispatched")
                {
                    TenantId = Constants.DefaultTenantId,
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);

                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Objective an operator is already working",
                    Status = ObjectiveStatusEnum.Scoped,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { operatorVoyage.Id }
                }).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        IntervalMinutes = 1,
                        MaxConcurrentVoyages = 1
                    }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(
                    1,
                    scheduler.ActiveDispatchedCount,
                    "An operator-dispatched voyage must count toward the concurrency number.");
                AssertEqual(
                    0,
                    admiral.DispatchVoyageCallCount,
                    "The scheduler must not add autonomous work on top of an operator's voyage.");
            }).ConfigureAwait(false);

            await RunTest("Per-vessel limit dispatches only one objective on a shared vessel", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("shared-lane-vessel", "https://github.com/test/shared-lane.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);

                for (int i = 0; i < 2; i++)
                {
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Title = "Shared lane objective " + i,
                        Status = ObjectiveStatusEnum.Scoped,
                        AutoDispatchEnabled = true,
                        VesselIds = new List<string> { vessel.Id }
                    }).ConfigureAwait(false);
                }

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        IntervalMinutes = 1,
                        MaxConcurrentVoyages = 3,
                        MaxConcurrentVoyagesPerVessel = 1
                    }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchVoyageCallCount, "One vessel must consume only one scheduler lane.");
                AssertContains("vessel_skips=1", scheduler.LastResultSummary ?? String.Empty, "Summary should name the vessel-limited objective.");
            }).ConfigureAwait(false);

            await RunTest("Per-vessel limit still dispatches independent vessels in parallel", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                for (int i = 0; i < 2; i++)
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("independent-vessel-" + i, "https://github.com/test/independent-" + i + ".git")
                    {
                        TenantId = Constants.DefaultTenantId
                    }).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Title = "Independent objective " + i,
                        Status = ObjectiveStatusEnum.Scoped,
                        AutoDispatchEnabled = true,
                        VesselIds = new List<string> { vessel.Id }
                    }).ConfigureAwait(false);
                }

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        IntervalMinutes = 1,
                        MaxConcurrentVoyages = 3,
                        MaxConcurrentVoyagesPerVessel = 1
                    }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(2, admiral.DispatchVoyageCallCount, "Independent vessels should use separate scheduler lanes.");
            }).ConfigureAwait(false);

            await RunTest("Enabling the scheduler survives a restart", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                // A restart rebuilds the scheduler from the settings FILE. Enabling it in memory
                // only therefore reverts silently on the next Admiral start, and the campaign stops
                // with nothing to notice: the tool reported success and the setting was real until
                // the process ended.
                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = false,
                        IntervalMinutes = 15
                    }
                };

                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver, new RecordingAdmiralService(testDb.Driver), settings);

                scheduler.Enable();
                scheduler.SetMaxConcurrentVoyages(4);
                scheduler.SetMaxConcurrentVoyagesPerVessel(1);
                bool persisted = await scheduler.TryPersistAsync().ConfigureAwait(false);
                AssertTrue(persisted, "Persisting the scheduler state should report success.");

                // Read the FILE a restart would read, not the object that was just mutated.
                ArmadaSettings reloaded = await ArmadaSettings.LoadAsync().ConfigureAwait(false);

                AssertTrue(
                    reloaded.AutonomousObjectiveScheduler.Enabled,
                    "A scheduler enabled over MCP must still be enabled after a restart.");
                AssertEqual(
                    4,
                    reloaded.AutonomousObjectiveScheduler.MaxConcurrentVoyages,
                    "Concurrency set over MCP must survive a restart too.");
                AssertEqual(
                    1,
                    reloaded.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel,
                    "Per-vessel concurrency must survive a restart too.");
            }).ConfigureAwait(false);

            await RunTest("A scheduler-dispatched voyage arms the vessel's Build and UnitTest Checks", async () =>
            {
                // The scheduler dispatches through the admiral directly rather than through
                // VoyageDispatchService. It armed nothing for months because the arming lived in
                // that one caller, so every autonomous voyage reached its Judge with no Check
                // attached and the operator had to attach one by hand.
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("arming-vessel", "https://github.com/test/arming.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);

                await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                {
                    TenantId = Constants.DefaultTenantId,
                    Name = "arming-profile",
                    Active = true,
                    Scope = WorkflowProfileScopeEnum.Vessel,
                    VesselId = vessel.Id,
                    BuildCommand = "dotnet build",
                    UnitTestCommand = "dotnet test"
                }).ConfigureAwait(false);

                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Autonomous work that needs a gate",
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
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);
                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchVoyageCallCount, "The sweep should dispatch exactly one voyage.");

                EnumerationResult<CheckRun> armed = await testDb.Driver.CheckRuns.EnumerateAsync(new CheckRunQuery
                {
                    TenantId = Constants.DefaultTenantId,
                    VesselId = vessel.Id,
                    PageSize = 100
                }).ConfigureAwait(false);

                AssertEqual(2, armed.Objects.Count, "A scheduler voyage must arm the same Checks an operator dispatch arms.");
                AssertTrue(armed.Objects.Exists(c => c.Type == CheckRunTypeEnum.Build), "Build must be armed.");
                AssertTrue(armed.Objects.Exists(c => c.Type == CheckRunTypeEnum.UnitTest), "UnitTest must be armed.");

                foreach (CheckRun run in armed.Objects)
                {
                    // The armed state carries no branch and an UNSTAMPED command on purpose. The
                    // executor stamps both once a stage has committed, so the Check measures that
                    // work rather than the default branch. Reading this record as a broken stub is
                    // the misdiagnosis to avoid: CheckRun.Command defaults to "echo" and rejects
                    // empty, so a freshly armed record always reads "echo" and never wrote it.
                    AssertEqual(CheckRunStatusEnum.Pending, run.Status, "An armed Check starts Pending.");
                    AssertTrue(String.IsNullOrEmpty(run.BranchName), "An armed Check carries no branch until it is stamped.");
                    AssertTrue(
                        run.Command != "dotnet build" && run.Command != "dotnet test",
                        "An armed Check must not carry the profile command until the executor stamps it.");
                }
            }).ConfigureAwait(false);

            await RunTest("Arming disabled in settings arms nothing on the scheduler path too", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("arming-off-vessel", "https://github.com/test/armingoff.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);

                await testDb.Driver.WorkflowProfiles.CreateAsync(new WorkflowProfile
                {
                    TenantId = Constants.DefaultTenantId,
                    Name = "arming-off-profile",
                    Active = true,
                    Scope = WorkflowProfileScopeEnum.Vessel,
                    VesselId = vessel.Id,
                    BuildCommand = "dotnet build",
                    UnitTestCommand = "dotnet test"
                }).ConfigureAwait(false);

                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Autonomous work with arming disabled",
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
                    },
                    VoyageCheckArming = new VoyageCheckArmingSettings { Enabled = false }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);
                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchVoyageCallCount, "The sweep should still dispatch.");

                EnumerationResult<CheckRun> armed = await testDb.Driver.CheckRuns.EnumerateAsync(new CheckRunQuery
                {
                    TenantId = Constants.DefaultTenantId,
                    VesselId = vessel.Id,
                    PageSize = 100
                }).ConfigureAwait(false);

                AssertEqual(0, armed.Objects.Count, "Disabling arming must disable it on every dispatch path.");
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
