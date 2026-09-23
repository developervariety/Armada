namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Server.Mcp.Tools;
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
            await RunTest("SweepAsync_SharedPreviewReportsCompleteDependencyBlock", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "dependency-preview-vessel", "https://github.com/test/repo.git")).ConfigureAwait(false);
                Objective blocker = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Transitive blocker",
                    Status = ObjectiveStatusEnum.InProgress
                }).ConfigureAwait(false);
                Objective candidate = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Blocked candidate",
                    Status = ObjectiveStatusEnum.Planned,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id },
                    BlockedByObjectiveIds = new List<string> { blocker.Id }
                }).ConfigureAwait(false);
                RecordingObjectiveDispatchPreview preview = new RecordingObjectiveDispatchPreview
                {
                    Result = new ObjectiveDispatchPreview
                    {
                        ObjectiveId = candidate.Id,
                        VesselId = vessel.Id,
                        IsReady = false,
                        BlockingChains = new List<List<string>>
                        {
                            new List<string> { candidate.Id, blocker.Id }
                        },
                        Issues = new List<ObjectiveDispatchPreviewIssue>
                        {
                            new ObjectiveDispatchPreviewIssue
                            {
                                Code = "objective_dependencies_incomplete",
                                Area = "admission",
                                Severity = ReadinessSeverityEnum.Error,
                                Message = "Dependency incomplete."
                            }
                        }
                    }
                };
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver, admiral, EnabledSchedulerSettings(), objectiveDispatchPreview: preview);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual("dependency_blocked=1", scheduler.LastSkipReason);
                AssertEqual(0, admiral.DispatchVoyageCallCount, "A dependency-blocked objective must not dispatch.");
                AssertEqual(1, preview.CallCount, "The scheduler evaluates the shared preview once.");
                List<ArmadaEvent> events = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_dependency")
                    .ConfigureAwait(false);
                AssertEqual(1, events.Count);
                AssertContains(candidate.Id + " -> " + blocker.Id, events[0].Message,
                    "The scheduler event must include the complete blocking chain.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_ZeroGlobalCapacityStopsBeforeCandidatePreflight", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "dependency-zero-capacity", "https://github.com/test/dependency-zero-capacity.git")).ConfigureAwait(false);
                Voyage activeVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Active capacity consumer")
                {
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Active capacity mission")
                {
                    VoyageId = activeVoyage.Id,
                    VesselId = vessel.Id,
                    Status = MissionStatusEnum.InProgress
                }).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Active objective",
                    Status = ObjectiveStatusEnum.InProgress,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { activeVoyage.Id }
                }).ConfigureAwait(false);
                Objective blocker = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Incomplete dependency",
                    Status = ObjectiveStatusEnum.InProgress
                }).ConfigureAwait(false);
                Objective candidate = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Blocked at zero capacity",
                    Status = ObjectiveStatusEnum.Planned,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id },
                    BlockedByObjectiveIds = new List<string> { blocker.Id }
                }).ConfigureAwait(false);
                RecordingObjectiveDispatchPreview preview = DependencyBlockedPreview(candidate, blocker, vessel);
                ArmadaSettings settings = EnabledSchedulerSettings();
                settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 1;
                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    settings,
                    objectiveDispatchPreview: preview);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual("max_concurrent", scheduler.LastSkipReason,
                    "A full fleet must report its admission limit.");
                AssertEqual(0, preview.CallCount, "A full fleet must not spend time on candidate preflight.");
                List<ArmadaEvent> events = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_dependency")
                    .ConfigureAwait(false);
                AssertEqual(0, events.Count, "No candidate was examined after fleet capacity was full.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_BusySiblingLaneStopsCandidateBeforePreflight", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel producer = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "dependency-lane-producer", "https://github.com/test/dependency-lane-producer.git")).ConfigureAwait(false);
                Vessel consumer = new Vessel(
                    "dependency-lane-consumer", "https://github.com/test/dependency-lane-consumer.git");
                consumer.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                {
                    new SiblingRepo
                    {
                        VesselRef = producer.Id,
                        RelativePath = "../dependency-lane-producer",
                        BuildParticipant = true
                    }
                });
                consumer = await testDb.Driver.Vessels.CreateAsync(consumer).ConfigureAwait(false);
                Voyage activeVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Active lane consumer")
                {
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Active lane mission")
                {
                    VoyageId = activeVoyage.Id,
                    VesselId = producer.Id,
                    Status = MissionStatusEnum.InProgress
                }).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Active producer objective",
                    Status = ObjectiveStatusEnum.InProgress,
                    VesselIds = new List<string> { producer.Id },
                    VoyageIds = new List<string> { activeVoyage.Id }
                }).ConfigureAwait(false);
                Objective blocker = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Incomplete lane dependency",
                    Status = ObjectiveStatusEnum.InProgress
                }).ConfigureAwait(false);
                Objective candidate = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Blocked on busy lane",
                    Status = ObjectiveStatusEnum.Planned,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { consumer.Id },
                    BlockedByObjectiveIds = new List<string> { blocker.Id }
                }).ConfigureAwait(false);
                RecordingObjectiveDispatchPreview preview = DependencyBlockedPreview(candidate, blocker, consumer);
                ArmadaSettings settings = EnabledSchedulerSettings();
                settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = 1;
                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    settings,
                    objectiveDispatchPreview: preview);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertContains("lane_busy:", scheduler.LastSkipReason ?? String.Empty,
                    "A full sibling lane must report its admission limit.");
                AssertEqual(0, preview.CallCount, "A full sibling lane must not spend time on candidate preflight.");
                List<ArmadaEvent> dependencyEvents = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_dependency")
                    .ConfigureAwait(false);
                AssertEqual(0, dependencyEvents.Count);
                List<ArmadaEvent> laneEvents = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_lane_busy")
                    .ConfigureAwait(false);
                AssertEqual(1, laneEvents.Count, "The lane gate reports the skipped candidate.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_DispatchesEarlyCandidateWithoutPreviewingLaterCandidates", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "large-candidate-set", "https://github.com/test/large-candidate-set.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                for (int i = 0; i < 250; i++)
                {
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "Candidate " + i.ToString("D3"),
                        Status = ObjectiveStatusEnum.Planned,
                        AutoDispatchEnabled = true,
                        Rank = i,
                        VesselIds = new List<string> { vessel.Id }
                    }).ConfigureAwait(false);
                }

                RecordingObjectiveDispatchPreview preview = new RecordingObjectiveDispatchPreview();
                preview.Handler = (objective, _) =>
                {
                    return Task.FromResult(new ObjectiveDispatchPreview
                    {
                        ObjectiveId = objective.Id,
                        VesselId = vessel.Id,
                        IsReady = true
                    });
                };
                ArmadaSettings settings = EnabledSchedulerSettings();
                settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 1;
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver, admiral, settings, objectiveDispatchPreview: preview);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, preview.CallCount, "The sweep stops preflight when fleet capacity is filled. Summary: " + scheduler.LastResultSummary + "; error: " + scheduler.LastSweepError);
                AssertEqual(1, admiral.DispatchVoyageCallCount, "The first ready candidate dispatches immediately.");
                AssertEqual(1, scheduler.SweepCandidatesExamined);
                AssertEqual(1, scheduler.SweepDispatchedCount);
                AssertEqual(250, scheduler.SweepCandidateCount);
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_ReportsLiveProgressCompletionAndFailure", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "progress-vessel", "https://github.com/test/progress-vessel.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                Objective candidate = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Progress candidate",
                    Status = ObjectiveStatusEnum.Planned,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id }
                }).ConfigureAwait(false);
                TaskCompletionSource<bool> previewEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> releasePreview = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                RecordingObjectiveDispatchPreview preview = new RecordingObjectiveDispatchPreview
                {
                    Handler = async (objective, token) =>
                    {
                        previewEntered.TrySetResult(true);
                        await releasePreview.Task.WaitAsync(token).ConfigureAwait(false);
                        return new ObjectiveDispatchPreview
                        {
                            ObjectiveId = objective.Id,
                            VesselId = vessel.Id,
                            IsReady = true
                        };
                    }
                };
                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    EnabledSchedulerSettings(),
                    objectiveDispatchPreview: preview);

                Task sweep = scheduler.SweepAsync();
                await previewEntered.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                ObjectiveSchedulerStatus running = McpObjectiveSchedulerTools.BuildStatus(scheduler);
                AssertTrue(running.SweepInProgress, "Status identifies the active sweep.");
                AssertTrue(running.LastSweepStartedUtc.HasValue, "Status records when the active sweep started.");
                AssertNull(running.LastSweepCompletedUtc, "An active sweep has no completion time.");
                AssertEqual(1, running.SweepCandidatesExamined, "Status reports candidate progress during preflight.");
                AssertEqual(1, running.SweepCandidateCount, "Status reports the total candidate count during preflight.");
                AssertEqual(0, running.SweepDispatchedCount);

                releasePreview.TrySetResult(true);
                await sweep.ConfigureAwait(false);
                ObjectiveSchedulerStatus completed = McpObjectiveSchedulerTools.BuildStatus(scheduler);
                AssertFalse(completed.SweepInProgress, "Status identifies the completed sweep.");
                AssertTrue(completed.LastSweepCompletedUtc.HasValue, "Status records completion.");
                AssertEqual(1, completed.SweepDispatchedCount, "Status reports the completed dispatch count. Summary: " + scheduler.LastResultSummary + "; error: " + scheduler.LastSweepError);
                AssertNull(completed.LastSweepError);

                Objective failingCandidate = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Failing preview candidate",
                    Status = ObjectiveStatusEnum.Planned,
                    AutoDispatchEnabled = true,
                    Rank = -1,
                    VesselIds = new List<string> { vessel.Id }
                }).ConfigureAwait(false);
                RecordingObjectiveDispatchPreview failingPreview = new RecordingObjectiveDispatchPreview
                {
                    Handler = (_, _) => throw new InvalidOperationException("preview failed")
                };
                AutonomousObjectiveScheduler failingScheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    EnabledSchedulerSettings(),
                    objectiveDispatchPreview: failingPreview);

                await AssertThrowsAsync<InvalidOperationException>(() => failingScheduler.SweepAsync()).ConfigureAwait(false);
                ObjectiveSchedulerStatus failed = McpObjectiveSchedulerTools.BuildStatus(failingScheduler);
                AssertFalse(failed.SweepInProgress, "A failed sweep releases its running state.");
                AssertTrue(failed.LastSweepCompletedUtc.HasValue, "A failed sweep records its terminal time.");
                AssertContains("preview failed", failed.LastSweepError ?? String.Empty);
                AssertContains("failed", failingScheduler.LastResultSummary ?? String.Empty);
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_BoundsCandidateWorkAndCoalescesConcurrentFollowUp", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "bounded-sweep-vessel", "https://github.com/test/bounded-sweep-vessel.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                for (int i = 0; i < 5; i++)
                {
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "Blocked candidate " + i,
                        Status = ObjectiveStatusEnum.Planned,
                        AutoDispatchEnabled = true,
                        Rank = i,
                        VesselIds = new List<string> { vessel.Id }
                    }).ConfigureAwait(false);
                }

                TaskCompletionSource<bool> firstPreviewEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> releaseFirstPreview = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                RecordingObjectiveDispatchPreview preview = new RecordingObjectiveDispatchPreview
                {
                    Handler = async (objective, token) =>
                    {
                        if (!firstPreviewEntered.Task.IsCompleted)
                        {
                            firstPreviewEntered.TrySetResult(true);
                            await releaseFirstPreview.Task.WaitAsync(token).ConfigureAwait(false);
                        }
                        return new ObjectiveDispatchPreview
                        {
                            ObjectiveId = objective.Id,
                            VesselId = vessel.Id,
                            IsReady = false,
                            Issues = new List<ObjectiveDispatchPreviewIssue>
                            {
                                new ObjectiveDispatchPreviewIssue
                                {
                                    Code = "objective_dependencies_incomplete",
                                    Area = "admission",
                                    Severity = ReadinessSeverityEnum.Error,
                                    Message = "Dependency incomplete."
                                }
                            }
                        };
                    }
                };
                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    EnabledSchedulerSettings(),
                    objectiveDispatchPreview: preview,
                    refillDebounceDelay: TimeSpan.FromMilliseconds(20),
                    maxCandidatesPerSweep: 2,
                    sweepTimeBudget: TimeSpan.FromSeconds(5));

                try
                {
                    Task firstSweep = scheduler.SweepAsync();
                    await firstPreviewEntered.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    await Task.WhenAll(scheduler.SweepAsync(), scheduler.SweepAsync()).ConfigureAwait(false);
                    releaseFirstPreview.TrySetResult(true);
                    await firstSweep.ConfigureAwait(false);
                    await WaitForEventTriggeredSweepCountAsync(scheduler, 1).ConfigureAwait(false);
                    DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                    while ((preview.CallCount < 4 || scheduler.SweepInProgress) && DateTime.UtcNow < deadline)
                        await Task.Delay(10).ConfigureAwait(false);

                    AssertEqual(4, preview.CallCount,
                        "Two concurrent triggers coalesce into one follow-up, with two bounded candidates per pass.");
                    AssertEqual(1L, scheduler.EventTriggeredSweepCount);
                    AssertTrue(scheduler.LastSweepBoundReached, "The follow-up also reports its candidate bound.");
                    AssertEqual(2, scheduler.SweepCandidatesExamined);
                    AssertContains("sweep_bound", scheduler.LastSkipReason ?? String.Empty);
                }
                finally
                {
                    scheduler.Dispose();
                }
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_TimeBudgetCancelsSlowCandidatePreflight", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "time-bounded-sweep-vessel", "https://github.com/test/time-bounded-sweep-vessel.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Slow preflight candidate",
                    Status = ObjectiveStatusEnum.Planned,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id }
                }).ConfigureAwait(false);
                RecordingObjectiveDispatchPreview preview = new RecordingObjectiveDispatchPreview
                {
                    Handler = async (_, token) =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                        return new ObjectiveDispatchPreview { IsReady = false };
                    }
                };
                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    EnabledSchedulerSettings(),
                    objectiveDispatchPreview: preview,
                    maxCandidatesPerSweep: 100,
                    sweepTimeBudget: TimeSpan.FromMilliseconds(40));

                DateTime started = DateTime.UtcNow;
                await scheduler.SweepAsync().ConfigureAwait(false);
                TimeSpan elapsed = DateTime.UtcNow - started;

                AssertTrue(elapsed < TimeSpan.FromSeconds(2), "A cooperative slow preflight is canceled at the sweep deadline.");
                AssertEqual(1, preview.CallCount);
                AssertTrue(scheduler.LastSweepBoundReached);
                AssertContains("sweep_bound", scheduler.LastSkipReason ?? String.Empty);
                AssertNull(scheduler.LastSweepError, "The configured work bound is not a scheduler failure.");
            }).ConfigureAwait(false);

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

            await RunTest("RequestRefill_RapidRequests_CoalesceAndBypassPeriodicInterval", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ArmadaSettings settings = EnabledSchedulerSettings();
                settings.AutonomousObjectiveScheduler.IntervalMinutes = 1440;
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    admiral,
                    settings,
                    refillDebounceDelay: TimeSpan.FromMilliseconds(50));

                try
                {
                    await scheduler.SweepAsync().ConfigureAwait(false);
                    AssertEqual(0L, scheduler.EventTriggeredSweepCount);

                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                        "event-refill-vessel", "https://github.com/test/event-refill.git")).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        Title = "Event refill candidate",
                        Status = ObjectiveStatusEnum.Scoped,
                        AutoDispatchEnabled = true,
                        VesselIds = new List<string> { vessel.Id }
                    }).ConfigureAwait(false);

                    DateTime? periodicSweepCompletedUtc = scheduler.LastSweepCompletedUtc;
                    scheduler.RequestRefill();
                    scheduler.RequestRefill();
                    scheduler.RequestRefill();

                    // The counter moves when the event-triggered sweep starts; its dispatch is visible only once
                    // that sweep has completed.
                    await WaitForEventTriggeredSweepCountAsync(scheduler, 1).ConfigureAwait(false);
                    await WaitForSweepCompletedAfterAsync(scheduler, periodicSweepCompletedUtc).ConfigureAwait(false);
                    await Task.Delay(100).ConfigureAwait(false);

                    AssertEqual(1L, scheduler.EventTriggeredSweepCount,
                        "Rapid refill requests must produce one event-triggered sweep.");
                    ObjectiveSchedulerStatus status = McpObjectiveSchedulerTools.BuildStatus(scheduler);
                    AssertEqual(1L, status.EventTriggeredSweepCount,
                        "Scheduler status must expose event-triggered sweep activity.");
                    AssertEqual(1, admiral.DispatchVoyageCallCount,
                        "The event-triggered sweep must bypass the 24-hour periodic interval.");
                }
                finally
                {
                    scheduler.Dispose();
                }
            }).ConfigureAwait(false);

            await RunTest("NotifyObjectiveChanged_RequestsOnlyForDispatchableOrCompletedObjectives", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    EnabledSchedulerSettings(),
                    refillDebounceDelay: TimeSpan.FromMilliseconds(40));

                try
                {
                    scheduler.NotifyObjectiveChanged(new Objective
                    {
                        Status = ObjectiveStatusEnum.Draft,
                        AutoDispatchEnabled = true
                    });
                    scheduler.NotifyObjectiveChanged(new Objective
                    {
                        Status = ObjectiveStatusEnum.Scoped,
                        AutoDispatchEnabled = false
                    });

                    await Task.Delay(120).ConfigureAwait(false);
                    AssertEqual(0L, scheduler.EventTriggeredSweepCount,
                        "Draft and non-autonomous objective changes must not request a refill.");

                    scheduler.NotifyObjectiveChanged(new Objective
                    {
                        Status = ObjectiveStatusEnum.Scoped,
                        AutoDispatchEnabled = true
                    });
                    scheduler.NotifyObjectiveChanged(new Objective
                    {
                        Status = ObjectiveStatusEnum.Planned,
                        AutoDispatchEnabled = true
                    });

                    await WaitForEventTriggeredSweepCountAsync(scheduler, 1).ConfigureAwait(false);
                    AssertEqual(1L, scheduler.EventTriggeredSweepCount,
                        "Ready Scoped and Planned objective changes must request a coalesced refill.");

                    scheduler.NotifyObjectiveChanged(new Objective
                    {
                        Status = ObjectiveStatusEnum.Completed,
                        AutoDispatchEnabled = false
                    });

                    await WaitForEventTriggeredSweepCountAsync(scheduler, 2).ConfigureAwait(false);
                    AssertEqual(2L, scheduler.EventTriggeredSweepCount,
                        "A completed objective must request a refill for its dependants.");
                }
                finally
                {
                    scheduler.Dispose();
                }
            }).ConfigureAwait(false);

            await RunTest("Dispose_CancelsPendingRefill", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    EnabledSchedulerSettings(),
                    refillDebounceDelay: TimeSpan.FromMinutes(1));

                scheduler.RequestRefill();
                scheduler.Dispose();

                AssertEqual(0L, scheduler.EventTriggeredSweepCount,
                    "Disposal must cancel a refill that is still in its debounce window.");
            }).ConfigureAwait(false);

            await RunTest("FairShare_RotatesCampaignsAcrossSweepsWithoutCrossingPriorityBands", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Objective campaignA = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Campaign A",
                    Tags = new List<string> { "campaign:a" },
                    Status = ObjectiveStatusEnum.Planned
                }).ConfigureAwait(false);
                Objective campaignB = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Campaign B",
                    Tags = new List<string> { "campaign:b" },
                    Status = ObjectiveStatusEnum.Planned
                }).ConfigureAwait(false);
                Vessel vesselA1 = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "fair-a1", "https://github.com/test/fair-a1.git") { TenantId = Constants.DefaultTenantId }).ConfigureAwait(false);
                Vessel vesselA2 = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "fair-a2", "https://github.com/test/fair-a2.git") { TenantId = Constants.DefaultTenantId }).ConfigureAwait(false);
                Vessel vesselB1 = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "fair-b1", "https://github.com/test/fair-b1.git") { TenantId = Constants.DefaultTenantId }).ConfigureAwait(false);
                Objective a1 = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "A1",
                    ParentObjectiveId = campaignA.Id,
                    Status = ObjectiveStatusEnum.Planned,
                    AutoDispatchEnabled = true,
                    Priority = ObjectivePriorityEnum.P0,
                    Rank = 1,
                    VesselIds = new List<string> { vesselA1.Id }
                }).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "A2",
                    ParentObjectiveId = campaignA.Id,
                    Status = ObjectiveStatusEnum.Planned,
                    AutoDispatchEnabled = true,
                    Priority = ObjectivePriorityEnum.P0,
                    Rank = 2,
                    VesselIds = new List<string> { vesselA2.Id }
                }).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "B1",
                    ParentObjectiveId = campaignB.Id,
                    Status = ObjectiveStatusEnum.Planned,
                    AutoDispatchEnabled = true,
                    Priority = ObjectivePriorityEnum.P0,
                    Rank = 3,
                    VesselIds = new List<string> { vesselB1.Id }
                }).ConfigureAwait(false);

                ArmadaSettings settings = EnabledSchedulerSettings();
                settings.AutonomousObjectiveScheduler.MaxConcurrentVoyages = 1;
                settings.AutonomousObjectiveScheduler.MaxConcurrentVoyagesPerVessel = 1;
                settings.AutonomousObjectiveScheduler.FairShareWithinPriorityBands = true;
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    admiral,
                    settings,
                    refillDebounceDelay: TimeSpan.FromMilliseconds(20));

                try
                {
                    AssertEqual(1, scheduler.MaxConcurrentVoyages,
                        "The fair-share test scheduler must use one global slot.");
                    await scheduler.SweepAsync().ConfigureAwait(false);
                    AssertEqual(1, admiral.DispatchedTitles.Count,
                        "The global capacity must permit one dispatch in the first sweep.");
                    AssertEqual("A1", admiral.DispatchedTitles[0],
                        "Rank order selects campaign A on the first sweep.");

                    Objective dispatchedA1 = (await testDb.Driver.Objectives.ReadAsync(a1.Id).ConfigureAwait(false))!;
                    Voyage firstVoyage = (await testDb.Driver.Voyages.ReadAsync(dispatchedA1.VoyageIds.Single()).ConfigureAwait(false))!;
                    firstVoyage.Status = VoyageStatusEnum.Complete;
                    await testDb.Driver.Voyages.UpdateAsync(firstVoyage).ConfigureAwait(false);

                    scheduler.RequestRefill();
                    DateTime refillDeadline = DateTime.UtcNow.AddSeconds(3);
                    ObjectiveSchedulerStatus status = McpObjectiveSchedulerTools.BuildStatus(scheduler);
                    string expectedCursor = "campaign:" + campaignB.Id;
                    while ((!status.LastServedCampaignByPriority.TryGetValue("P0", out string? cursor)
                            || cursor != expectedCursor)
                        && DateTime.UtcNow < refillDeadline)
                    {
                        await Task.Delay(10).ConfigureAwait(false);
                        status = McpObjectiveSchedulerTools.BuildStatus(scheduler);
                    }

                    AssertEqual("A1,B1", String.Join(',', admiral.DispatchedTitles),
                        "The next sweep must start after the last served campaign, even when A2 has the lower rank.");
                    AssertEqual(expectedCursor, status.LastServedCampaignByPriority["P0"],
                        "Status must expose the campaign cursor after the successful refill dispatch.");
                }
                finally
                {
                    scheduler.Dispose();
                }
            }).ConfigureAwait(false);

            await RunTest("Pause records who, when and why; Resume drops all three; both are mirrored for persistence", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ArmadaSettings settings = new ArmadaSettings();
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, new RecordingAdmiralService(testDb.Driver), settings);

                DateTime before = DateTime.UtcNow;
                scheduler.Pause("  deploy-session  ", " protecting a rebuild ");
                AssertTrue(scheduler.Paused);
                AssertEqual("deploy-session", scheduler.PausedBy);
                AssertEqual("protecting a rebuild", scheduler.PauseReason);
                AssertTrue(scheduler.PausedUtc.HasValue && scheduler.PausedUtc.Value >= before, "PausedUtc is stamped at pause time.");

                await scheduler.TryPersistAsync().ConfigureAwait(false);
                AssertEqual("deploy-session", settings.AutonomousObjectiveScheduler.PausedBy, "Attribution is mirrored into settings before the write.");
                AssertEqual(scheduler.PausedUtc, settings.AutonomousObjectiveScheduler.PausedUtc);
                AssertEqual("protecting a rebuild", settings.AutonomousObjectiveScheduler.PauseReason);

                scheduler.Resume();
                AssertFalse(scheduler.Paused);
                AssertTrue(scheduler.PausedBy == null && scheduler.PausedUtc == null && scheduler.PauseReason == null, "Resume drops the attribution.");
                await scheduler.TryPersistAsync().ConfigureAwait(false);
                AssertTrue(settings.AutonomousObjectiveScheduler.PausedBy == null && settings.AutonomousObjectiveScheduler.PausedUtc == null, "Cleared attribution is mirrored too.");

                scheduler.Pause();
                AssertTrue(scheduler.Paused && scheduler.PausedBy == null, "An unattributed pause is still a pause; it is the rule that refuses to clear it.");
            }).ConfigureAwait(false);

            await RunTest("Pause attribution loads from settings at construction, and the absence threshold has a floor of 30", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                ArmadaSettings settings = new ArmadaSettings();
                settings.AutonomousObjectiveScheduler.Paused = true;
                settings.AutonomousObjectiveScheduler.PausedBy = "peer";
                settings.AutonomousObjectiveScheduler.PausedUtc = new DateTime(2026, 8, 24, 18, 4, 0, DateTimeKind.Utc);
                settings.AutonomousObjectiveScheduler.PauseReason = "deploy";
                settings.AutonomousObjectiveScheduler.StalePauseAbsenceMinutes = 5;
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, new RecordingAdmiralService(testDb.Driver), settings);

                AssertTrue(scheduler.Paused);
                AssertEqual("peer", scheduler.PausedBy);
                AssertEqual(settings.AutonomousObjectiveScheduler.PausedUtc, scheduler.PausedUtc);
                AssertEqual("deploy", scheduler.PauseReason);
                AssertEqual(30, scheduler.StalePauseAbsenceMinutes, "The owner-decided floor is 30 minutes; a smaller setting is clamped up.");
                settings.AutonomousObjectiveScheduler.StalePauseAbsenceMinutes = 2000;
                AssertEqual(1440, scheduler.StalePauseAbsenceMinutes, "Clamped to one day at the top.");
            }).ConfigureAwait(false);

            await RunTest("A malformed blocker list never dispatches its objective, and the sweep still serves the other rows", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Vessel blockedVessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("corrupt-blocker-vessel", "https://github.com/test/corrupt-blocker.git")
                    {
                        TenantId = Constants.DefaultTenantId
                    }).ConfigureAwait(false);
                    Vessel otherVessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("corrupt-blocker-other", "https://github.com/test/corrupt-blocker-other.git")
                    {
                        TenantId = Constants.DefaultTenantId
                    }).ConfigureAwait(false);
                    Objective blocker = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Title = "Unfinished blocker",
                        Status = ObjectiveStatusEnum.InProgress
                    }).ConfigureAwait(false);
                    Objective blocked = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Title = "Blocked by an unfinished objective",
                        Status = ObjectiveStatusEnum.Planned,
                        BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch,
                        AutoDispatchEnabled = true,
                        Priority = ObjectivePriorityEnum.P0,
                        BlockedByObjectiveIds = new List<string> { blocker.Id },
                        VesselIds = new List<string> { blockedVessel.Id }
                    }).ConfigureAwait(false);
                    Objective ready = await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Title = "Ready and unblocked",
                        Status = ObjectiveStatusEnum.Planned,
                        BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch,
                        AutoDispatchEnabled = true,
                        Priority = ObjectivePriorityEnum.P2,
                        VesselIds = new List<string> { otherVessel.Id }
                    }).ConfigureAwait(false);

                    using (Microsoft.Data.Sqlite.SqliteConnection connection = new Microsoft.Data.Sqlite.SqliteConnection(testDb.ConnectionString))
                    {
                        await connection.OpenAsync().ConfigureAwait(false);
                        using (Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand())
                        {
                            command.CommandText = "UPDATE objectives SET blocked_by_objective_ids_json = @value WHERE id = @id;";
                            command.Parameters.AddWithValue("@value", "[\"" + blocker.Id);
                            command.Parameters.AddWithValue("@id", blocked.Id);
                            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }

                    RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                    AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, EnabledSchedulerSettings());
                    await scheduler.SweepAsync().ConfigureAwait(false);

                    AssertEqual(1, admiral.DispatchVoyageCallCount, "Only the readable, unblocked objective dispatches.");
                    AssertEqual(ready.Title, admiral.DispatchedTitles.Count > 0 ? admiral.DispatchedTitles[0] : null);
                    AssertFalse(admiral.DispatchedTitles.Contains(blocked.Title),
                        "An objective whose blocker list cannot be read is never dispatched as unblocked.");
                }
            }).ConfigureAwait(false);

            await RunTest("A settings file edit reaches the scheduler on the next tick and a later tool change never reverts it", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string directory = Path.Combine(Path.GetTempPath(), "armada-scheduler-reload-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(directory);
                    try
                    {
                        string path = Path.Combine(directory, "settings.json");
                        ArmadaSettings live = EnabledSchedulerSettings();
                        live.SettingsFilePath = path;
                        await live.SaveAsync().ConfigureAwait(false);
                        SettingsReloadService reload = new SettingsReloadService(live, _ => Task.FromResult(new List<Captain>()));
                        AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, new RecordingAdmiralService(testDb.Driver), live);
                        AssertEqual(1, scheduler.IntervalMinutes);

                        ArmadaSettings edited = await ArmadaSettings.LoadAsync(path).ConfigureAwait(false);
                        edited.AutonomousObjectiveScheduler.IntervalMinutes = 30;
                        edited.AutonomousObjectiveScheduler.Paused = true;
                        edited.AutonomousObjectiveScheduler.PausedBy = "file-editor";
                        edited.AutonomousObjectiveScheduler.PauseReason = "edited in the file";
                        await edited.SaveAsync(path).ConfigureAwait(false);
                        SettingsReloadResult result = await reload.ReloadAsync().ConfigureAwait(false);
                        AssertTrue(result.Applied, "The edited file is a valid candidate: " + result.Reason);

                        AssertEqual(30, scheduler.IntervalMinutes, "The edited interval is the scheduler's interval after the reload.");
                        AssertTrue(scheduler.Paused, "The edited pause is the scheduler's pause after the reload.");
                        AssertEqual("file-editor", scheduler.PausedBy);
                        await scheduler.SweepAsync().ConfigureAwait(false);
                        AssertEqual("skipped (paused)", scheduler.LastResultSummary, "The next tick obeys the edited pause.");

                        scheduler.SetMaxConcurrentVoyages(4);
                        AssertTrue(await scheduler.TryPersistAsync().ConfigureAwait(false));
                        ArmadaSettings onDisk = await ArmadaSettings.LoadAsync(path).ConfigureAwait(false);
                        AssertEqual(4, onDisk.AutonomousObjectiveScheduler.MaxConcurrentVoyages, "The tool change is written.");
                        AssertEqual(30, onDisk.AutonomousObjectiveScheduler.IntervalMinutes, "The tool write keeps the edited interval.");
                        AssertTrue(onDisk.AutonomousObjectiveScheduler.Paused, "The tool write keeps the edited pause.");
                        AssertEqual("edited in the file", onDisk.AutonomousObjectiveScheduler.PauseReason);

                        // A pause set through the tool survives a reload of the file it wrote and a restart.
                        scheduler.Pause("owner-session", "deploy window");
                        DateTime? pausedUtc = scheduler.PausedUtc;
                        AssertTrue(await scheduler.TryPersistAsync().ConfigureAwait(false));
                        AssertTrue((await reload.ReloadAsync().ConfigureAwait(false)).Applied);
                        AssertTrue(scheduler.Paused && scheduler.PausedBy == "owner-session" && scheduler.PauseReason == "deploy window" && scheduler.PausedUtc == pausedUtc,
                            "A reload of the persisted file keeps the pause and its attribution.");
                        AutonomousObjectiveScheduler restarted = CreateScheduler(testDb.Driver, new RecordingAdmiralService(testDb.Driver),
                            await ArmadaSettings.LoadAsync(path).ConfigureAwait(false));
                        AssertTrue(restarted.Paused && restarted.PausedBy == "owner-session" && restarted.PauseReason == "deploy window" && restarted.PausedUtc == pausedUtc,
                            "A restart keeps the pause and its attribution.");
                    }
                    finally
                    {
                        try { Directory.Delete(directory, true); } catch (IOException) { }
                    }
                }
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

                // The ceiling is raised so the live voyage does not fill the fleet lane first:
                // the point is that the per-row check names the live voyage as the reason.
                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        IntervalMinutes = 1,
                        MaxConcurrentVoyages = 3,
                        MaxConcurrentVoyagesPerVessel = 3
                    }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchVoyageCallCount, "Scheduler must not dispatch while a linked voyage is still live.");
                AssertContains("dispatched=0", scheduler.LastResultSummary ?? string.Empty, "Sweep summary should show zero dispatches.");
                AssertContains("active_voyage", scheduler.LastSkipReason ?? string.Empty, "A live linked voyage must be named as the skip reason, not dropped silently.");
            }).ConfigureAwait(false);

            await RunTest("A Scoped row whose linked voyages have all ended is a requeue and dispatches again", async () =>
            {
                // Linking a voyage promotes the objective to InProgress, so Scoped-with-voyages only
                // exists after an operator requeued it. Reconcile completes only InProgress rows,
                // so nothing else would ever release this row: holding it is a permanent silent skip.
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("requeue-vessel", "https://github.com/test/requeue.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);

                Voyage failedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Failed voyage")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);

                Objective requeued = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Requeued after a failed voyage",
                    Status = ObjectiveStatusEnum.Scoped,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id },
                    VoyageIds = new List<string> { failedVoyage.Id }
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

                AssertEqual(1, admiral.DispatchVoyageCallCount, "A requeued objective whose voyages have all ended must dispatch again.");
                AssertContains("dispatched=1", scheduler.LastResultSummary ?? string.Empty, "Sweep summary should count the requeue dispatch.");
                AssertNotNull(requeued.Id, "Objective fixture should have an id.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_EligibleObjectiveWithTwoVessels_RecordsObjectiveSkippedReason", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel first = await testDb.Driver.Vessels.CreateAsync(new Vessel("two-vessel-a", "https://github.com/test/a.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                Vessel second = await testDb.Driver.Vessels.CreateAsync(new Vessel("two-vessel-b", "https://github.com/test/b.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);

                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Reads two repositories, commits to one",
                    Status = ObjectiveStatusEnum.Scoped,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { first.Id, second.Id }
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

                AssertEqual(0, admiral.DispatchVoyageCallCount, "An objective with two vessels must not auto-dispatch.");
                AssertEqual("vessel_count=1", scheduler.LastSkipReason, "A sweep that refused every eligible objective must name the reason.");
                AssertContains("vessel_count=1", scheduler.LastResultSummary ?? string.Empty, "Sweep summary should count the objective-level skip.");

                List<ArmadaEvent> skippedEvents = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_vessel_count")
                    .ConfigureAwait(false);
                AssertEqual(1, skippedEvents.Count, "The vessel-count skip must be recorded as an objective event.");
                AssertContains(objective.Id, skippedEvents[0].Message ?? string.Empty, "The skip event must name the objective.");
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

            await RunTest("An unlinked operator voyage with repository work counts toward the concurrency limit", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(
                    new Vessel("concurrency-vessel", "https://github.com/test/conc.git")
                    {
                        TenantId = Constants.DefaultTenantId
                    }).ConfigureAwait(false);

                Voyage operatorVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("operator-dispatched")
                {
                    TenantId = Constants.DefaultTenantId,
                    Status = VoyageStatusEnum.InProgress
                }).ConfigureAwait(false);

                await testDb.Driver.Missions.CreateAsync(new Mission("operator mission", "Unlinked operator work.")
                {
                    TenantId = Constants.DefaultTenantId,
                    VoyageId = operatorVoyage.Id,
                    VesselId = vessel.Id,
                    Status = MissionStatusEnum.InProgress
                }).ConfigureAwait(false);

                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Independent autonomous objective",
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
                        MaxConcurrentVoyages = 1
                    }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(
                    1,
                    scheduler.ActiveDispatchedCount,
                    "An unlinked operator voyage with repository work must count toward the concurrency number.");
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
                AssertContains("vessel_concurrency=1", scheduler.LastResultSummary ?? String.Empty, "Summary should name the vessel-limited objective.");
            }).ConfigureAwait(false);

            await RunTest("Two vessels joined by a build-participating sibling are one lane: the second objective waits with lane_busy", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel producer = await testDb.Driver.Vessels.CreateAsync(new Vessel("lane-producer", "https://github.com/test/lane-producer.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                Vessel consumer = new Vessel("lane-consumer", "https://github.com/test/lane-consumer.git")
                {
                    TenantId = Constants.DefaultTenantId
                };
                consumer.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                {
                    new SiblingRepo
                    {
                        VesselRef = producer.Id,
                        RelativePath = "../Producer",
                        BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                        DefaultBranch = "main",
                        BuildParticipant = true,
                        ExtractionArtifactPaths = null
                    }
                });
                consumer = await testDb.Driver.Vessels.CreateAsync(consumer).ConfigureAwait(false);

                foreach (Vessel vessel in new[] { producer, consumer })
                {
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Title = "Lane objective on " + vessel.Name,
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

                AssertEqual(1, admiral.DispatchVoyageCallCount, "A lane holds one voyage at per-vessel ceiling 1, whichever vessel owns the objective.");
                AssertContains("lane_busy:", scheduler.LastResultSummary ?? String.Empty, "The skip names the lane, not a lone vessel.");
                AssertContains(producer.Id, scheduler.LastResultSummary ?? String.Empty, "The lane name carries the producer.");
                AssertContains(consumer.Id, scheduler.LastResultSummary ?? String.Empty, "The lane name carries the consumer.");
                List<ArmadaEvent> laneEvents = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_lane_busy")
                    .ConfigureAwait(false);
                AssertEqual(1, laneEvents.Count, "The lane skip is recorded as an objective event.");
            }).ConfigureAwait(false);

            await RunTest("A read-only sibling declaration forms no lane: both vessels dispatch", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel producer = await testDb.Driver.Vessels.CreateAsync(new Vessel("lane-producer", "https://github.com/test/lane-producer.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                Vessel consumer = new Vessel("lane-consumer", "https://github.com/test/lane-consumer.git")
                {
                    TenantId = Constants.DefaultTenantId
                };
                consumer.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                {
                    new SiblingRepo
                    {
                        VesselRef = producer.Id,
                        RelativePath = "../Producer",
                        BranchStrategy = SiblingBranchStrategyEnum.DefaultOnly,
                        DefaultBranch = "main",
                        BuildParticipant = false,
                        ExtractionArtifactPaths = new List<string> { "output/decompiled-src" }
                    }
                });
                consumer = await testDb.Driver.Vessels.CreateAsync(consumer).ConfigureAwait(false);

                foreach (Vessel vessel in new[] { producer, consumer })
                {
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Title = "Lane objective on " + vessel.Name,
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

                AssertEqual(2, admiral.DispatchVoyageCallCount, "A decompiled or artifact sibling must not make a research vessel wait for the port it reads.");
                AssertFalse((scheduler.LastResultSummary ?? String.Empty).Contains("lane_busy"), "No lane skip is reported for a read-only sibling.");
            }).ConfigureAwait(false);

            // An engaged dispatch hold is the hold working, not a fault. It is read once per tick and
            // named, so a real dispatch fault arriving during a deploy window still reads as one.
            await RunTest("An engaged dispatch hold is reported as dispatch_hold once per tick, never as dispatch_error", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                for (int i = 0; i < 2; i++)
                {
                    Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("hold-vessel-" + i, "https://github.com/test/hold-" + i + ".git")
                    {
                        TenantId = Constants.DefaultTenantId
                    }).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Title = "Held objective " + i,
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
                        MaxConcurrentVoyages = 3
                    }
                };

                DispatchHold hold = new DispatchHold();
                hold.Engage("redeploy window", "operator-session");
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings, hold);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchVoyageCallCount, "Nothing dispatches while the hold is engaged.");
                AssertEqual("dispatch_hold", scheduler.LastSkipReason, "The hold is reported by its own name.");
                AssertContains("dispatch_hold", scheduler.LastResultSummary ?? string.Empty, "The summary names the hold.");
                List<ArmadaEvent> holdEvents = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_dispatch_hold")
                    .ConfigureAwait(false);
                AssertEqual(1, holdEvents.Count, "One event per tick, not one per eligible objective (two were eligible).");

                hold.Clear();
                await scheduler.SweepAsync().ConfigureAwait(false);
                AssertEqual(0, admiral.DispatchVoyageCallCount, "The second sweep within the interval is a no-op; the hold cleared cleanly.");
            }).ConfigureAwait(false);

            await RunTest("A dispatch fault with no hold engaged still reads as dispatch_error", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("fault-vessel", "https://github.com/test/fault.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Faulting objective",
                    Status = ObjectiveStatusEnum.Scoped,
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
                admiral.ThrowOnDispatch = new InvalidOperationException("vessel path is unreadable");
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings, new DispatchHold());

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertContains("dispatch_error", scheduler.LastSkipReason ?? string.Empty, "A genuine dispatch fault keeps its name.");
            }).ConfigureAwait(false);

            await RunTest("An objective's start ref reaches the dispatch description", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("start-ref-vessel", "https://github.com/test/start.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                Vessel referenceConsumer = await testDb.Driver.Vessels.CreateAsync(new Vessel("ReferenceConsumer", "https://github.com/test/reference-consumer.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                vessel.SiblingRepos = JsonSerializer.Serialize(new List<SiblingRepo>
                {
                    new SiblingRepo { VesselRef = referenceConsumer.Id, RelativePath = "../ReferenceConsumer" }
                });
                await testDb.Driver.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Continue from the accepted tip",
                    Status = ObjectiveStatusEnum.Scoped,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id },
                    StartFromRef = "recover/accepted-tip-abc1234",
                    Preparation = new ObjectivePreparation
                    {
                        RequiredSiblingInputs = new List<ObjectivePreparationSiblingInput>
                        {
                            new ObjectivePreparationSiblingInput { VesselRef = referenceConsumer.Name, RelativePath = "../ReferenceConsumer" }
                        },
                        Claims = new List<ObjectivePreparationClaim>
                        {
                            new ObjectivePreparationClaim
                            {
                                Kind = ObjectivePreparationClaimKindEnum.DispatchEntryPoint,
                                Text = "Use the scheduler dispatch seam."
                            }
                        }
                    }
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

                AssertEqual(1, admiral.DispatchVoyageCallCount, "The objective dispatches.");
                AssertNotNull(admiral.LastMissionDescriptions, "The dispatch carried descriptions.");
                AssertEqual("recover/accepted-tip-abc1234", admiral.LastMissionDescriptions![0].StartFromRef, "The objective's start ref reaches the first-stage description.");
                AssertContains("<!-- armada-objective-brief:", admiral.LastMissionDescriptions[0].Description,
                    "The autonomous path must deliver the authoritative objective brief.");
                AssertContains("Use the scheduler dispatch seam.", admiral.LastMissionDescriptions[0].Description,
                    "Prepared research must reach the autonomous mission.");
                AssertContains("vessel `ReferenceConsumer` at `../ReferenceConsumer`", admiral.LastMissionDescriptions[0].Description,
                    "Structured sibling preparation must reach the autonomous mission.");
            }).ConfigureAwait(false);

            await RunTest("An operator-confirmed preparation stage skip reaches the scheduler dispatch", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("stage-skip-vessel", "https://github.com/test/skip.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Docs-only change with a confirmed skip",
                    Status = ObjectiveStatusEnum.Scoped,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id },
                    Preparation = new ObjectivePreparation
                    {
                        StageSkip = new StageSkipRequest
                        {
                            Stages = new List<string> { "TestEngineer" },
                            Reason = "docs-only change",
                            ConfirmedBy = "operator@example.com"
                        }
                    }
                }).ConfigureAwait(false);

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, EnabledSchedulerSettings());

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchVoyageCallCount, "The objective dispatches.");
                AssertNotNull(admiral.LastStageSkip, "The confirmed skip reaches the admiral.");
                AssertEqual("TestEngineer", admiral.LastStageSkip!.Stages[0], "The confirmed persona is passed through.");
                AssertEqual("operator@example.com", admiral.LastStageSkip.ConfirmedBy, "The confirmer travels with the skip.");
            }).ConfigureAwait(false);

            await RunTest("An unconfirmed preparation stage skip is a named skip, never a dispatch", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("unconfirmed-skip-vessel", "https://github.com/test/unconfirmed.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Skip with no confirmer",
                    Status = ObjectiveStatusEnum.Scoped,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id },
                    Preparation = new ObjectivePreparation
                    {
                        StageSkip = new StageSkipRequest { Stages = new List<string> { "TestEngineer" } }
                    }
                }).ConfigureAwait(false);

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, EnabledSchedulerSettings());

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(0, admiral.DispatchVoyageCallCount, "An unconfirmed skip must not dispatch.");
                AssertContains("stage_skip_unconfirmed", scheduler.LastSkipReason ?? string.Empty, "The skip is named.");
                List<ArmadaEvent> events = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_stage_skip_unconfirmed")
                    .ConfigureAwait(false);
                AssertEqual(1, events.Count, "The skip is recorded as an objective event.");
                AssertContains(objective.Id, events[0].Message ?? string.Empty, "The event names the objective.");
            }).ConfigureAwait(false);

            await RunTest("A start ref that does not resolve is reported as start_from_ref_missing, not dispatch_error", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("gone-ref-vessel", "https://github.com/test/gone.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Continue from a ref that is gone",
                    Status = ObjectiveStatusEnum.Scoped,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id },
                    StartFromRef = "recover/gone"
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
                admiral.ThrowOnDispatch = new StartFromRefMissingException("start_from_ref_missing: ref 'recover/gone' does not resolve in the repository of vessel gone-ref-vessel.");
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings, new DispatchHold());

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertContains("start_from_ref_missing", scheduler.LastSkipReason ?? string.Empty, "The skip is named after the ref, not the fleet.");
                AssertFalse((scheduler.LastSkipReason ?? string.Empty).Contains("dispatch_error"), "A bad ref is not a dispatch error.");

                List<ArmadaEvent> events = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.start_from_ref_missing")
                    .ConfigureAwait(false);
                AssertEqual(1, events.Count, "The skip is recorded as an objective event.");
                AssertContains(objective.Id, events[0].Message ?? string.Empty, "The event names the objective.");
                AssertContains("recover/gone", events[0].Message ?? string.Empty, "The event names the ref.");
            }).ConfigureAwait(false);

            await RunTest("A sweep that dispatches nothing says why", async () =>
            {
                // A multi-vessel objective is eligible by every rule the selector checks,
                // then fails the one-vessel requirement inside dispatch. That skip used to
                // be swallowed: dispatched=0, LastSkipReason=null, no event. Two objectives
                // sat permanently undispatchable and the sweep reported an idle fleet.
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel first = await testDb.Driver.Vessels.CreateAsync(new Vessel("multi-a", "https://github.com/test/multi-a.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                Vessel second = await testDb.Driver.Vessels.CreateAsync(new Vessel("multi-b", "https://github.com/test/multi-b.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);

                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Census vein spanning several vessels",
                    Status = ObjectiveStatusEnum.Scoped,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { first.Id, second.Id }
                }).ConfigureAwait(false);

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

                AssertEqual(0, admiral.DispatchVoyageCallCount, "A multi-vessel objective must not auto-dispatch.");
                AssertContains("vessel_count", scheduler.LastSkipReason ?? String.Empty,
                    "The sweep must name the reason it dispatched nothing.");
                AssertContains("vessel_count", scheduler.LastResultSummary ?? String.Empty,
                    "The summary must carry the skip breakdown.");
            }).ConfigureAwait(false);

            await RunTest("An empty backlog reports no_eligible_objectives, not silence", async () =>
            {
                // "Nothing to do" and "everything is blocked" are different states. A null
                // skip reason must mean work was dispatched, never that the sweep cannot say.
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

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

                AssertEqual(0, admiral.DispatchVoyageCallCount);
                AssertEqual("no_eligible_objectives", scheduler.LastSkipReason,
                    "An idle fleet must be reported as idle, not as an unexplained no-op.");
            }).ConfigureAwait(false);

            await RunTest("A sweep that dispatches clears the skip reason", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("single-lane", "https://github.com/test/single-lane.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);

                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Ordinary single-vessel objective",
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
                        MaxConcurrentVoyages = 3,
                        MaxConcurrentVoyagesPerVessel = 1
                    }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchVoyageCallCount);
                AssertNull(scheduler.LastSkipReason, "Work was dispatched, so there is nothing to explain.");
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

                AssertEqual(3, armed.Objects.Count, "A scheduler voyage must arm the same Checks an operator dispatch arms.");
                AssertTrue(armed.Objects.Exists(c => c.Type == CheckRunTypeEnum.Build), "Build must be armed.");
                AssertTrue(armed.Objects.Exists(c => c.Type == CheckRunTypeEnum.UnitTest), "UnitTest must be armed.");
                AssertTrue(armed.Objects.Exists(c => c.Type == CheckRunTypeEnum.Slop), "The profile invokes dotnet, so Slop must be armed.");

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

            await RunTest("ReconcileObjective_FailedOriginalWithCompletedRescue_ReconcilesToCompleted", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Voyage failedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Original voyage")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);

                Mission originalWorker = await testDb.Driver.Missions.CreateAsync(new Mission("Original worker")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    Status = MissionStatusEnum.WorkProduced
                }).ConfigureAwait(false);
                Mission failedMission = await testDb.Driver.Missions.CreateAsync(new Mission("Original judge")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    DependsOnMissionId = originalWorker.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);

                Voyage rescueVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Rescue voyage")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Complete
                }).ConfigureAwait(false);

                Mission rescueWorker = await testDb.Driver.Missions.CreateAsync(new Mission("Rescue worker")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = rescueVoyage.Id,
                    Description = RescueMissionMarker.Marker,
                    ParentMissionId = failedMission.Id,
                    Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Rescue judge")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = rescueVoyage.Id,
                    DependsOnMissionId = rescueWorker.Id,
                    Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);

                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Failed original, landed rescue",
                    Status = ObjectiveStatusEnum.InProgress,
                    VoyageIds = new List<string> { failedVoyage.Id, rescueVoyage.Id }
                }).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        IntervalMinutes = 1,
                        MaxConcurrentVoyages = 3,
                        MaxConcurrentVoyagesPerVessel = 3
                    }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);
                await scheduler.SweepAsync().ConfigureAwait(false);

                Objective? reconciled = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertEqual(ObjectiveStatusEnum.Completed, reconciled!.Status,
                    "An objective whose failed original voyage was re-done by a landed rescue voyage must reconcile to Completed, not sit InProgress forever.");
            }).ConfigureAwait(false);

            await RunTest("ReconcileObjective_OneOfTwoFailedChainsRescued_StaysInProgress", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Voyage failedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Two-chain original voyage")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);

                Mission workerA = await testDb.Driver.Missions.CreateAsync(new Mission("Completed Worker A")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);
                Mission workerB = await testDb.Driver.Missions.CreateAsync(new Mission("Completed Worker B")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);
                Mission rescuedFailure = await testDb.Driver.Missions.CreateAsync(new Mission("Failed Judge A")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    DependsOnMissionId = workerA.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);
                Mission unresolvedFailure = await testDb.Driver.Missions.CreateAsync(new Mission("Failed Judge B")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    DependsOnMissionId = workerB.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);

                Voyage rescueVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Chain A rescue voyage")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Complete
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Chain A rescue")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = rescueVoyage.Id,
                    Description = RescueMissionMarker.Marker,
                    ParentMissionId = rescuedFailure.Id,
                    Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);

                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Only one independent chain recovered",
                    Status = ObjectiveStatusEnum.InProgress,
                    VoyageIds = new List<string> { failedVoyage.Id, rescueVoyage.Id }
                }).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        IntervalMinutes = 1,
                        MaxConcurrentVoyages = 3,
                        MaxConcurrentVoyagesPerVessel = 3
                    }
                };
                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    settings);

                await scheduler.SweepAsync().ConfigureAwait(false);

                Objective? reconciled = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertEqual(ObjectiveStatusEnum.InProgress, reconciled!.Status,
                    "A completed rescue for chain A must not hide unresolved failed chain " + unresolvedFailure.Id + ".");
            }).ConfigureAwait(false);

            await RunTest("ReconcileObjective_EveryFailedChainRescued_ReconcilesToCompleted", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Voyage failedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Two recovered chains")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);
                Mission failureA = await testDb.Driver.Missions.CreateAsync(new Mission("Failed chain A")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);
                Mission failureB = await testDb.Driver.Missions.CreateAsync(new Mission("Failed chain B")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Cancelled dependent of chain A")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    DependsOnMissionId = failureA.Id,
                    Status = MissionStatusEnum.Cancelled
                }).ConfigureAwait(false);

                List<string> voyageIds = new List<string> { failedVoyage.Id };
                Voyage failedRescueAttempt = await testDb.Driver.Voyages.CreateAsync(new Voyage("Failed historical rescue")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);
                voyageIds.Add(failedRescueAttempt.Id);
                await testDb.Driver.Missions.CreateAsync(new Mission("Failed rescue for chain A")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedRescueAttempt.Id,
                    Description = RescueMissionMarker.Marker,
                    ParentMissionId = failureA.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);

                foreach (Mission failure in new[] { failureA, failureB })
                {
                    Voyage rescueVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Completed rescue for " + failure.Title)
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Status = VoyageStatusEnum.Complete
                    }).ConfigureAwait(false);
                    voyageIds.Add(rescueVoyage.Id);
                    await testDb.Driver.Missions.CreateAsync(new Mission("Rescue for " + failure.Title)
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        VoyageId = rescueVoyage.Id,
                        Description = RescueMissionMarker.Marker,
                        ParentMissionId = failure.Id,
                        Status = MissionStatusEnum.Complete
                    }).ConfigureAwait(false);
                }

                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Every independent chain recovered",
                    Status = ObjectiveStatusEnum.InProgress,
                    VoyageIds = voyageIds
                }).ConfigureAwait(false);
                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        IntervalMinutes = 1,
                        MaxConcurrentVoyages = 3,
                        MaxConcurrentVoyagesPerVessel = 3
                    }
                };
                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    settings);

                await scheduler.SweepAsync().ConfigureAwait(false);

                Objective? reconciled = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertEqual(ObjectiveStatusEnum.Completed, reconciled!.Status,
                    "The objective can complete after every failed chain has its own completed rescue.");
            }).ConfigureAwait(false);

            await RunTest("ReconcileObjective_TwoFailuresOnOneChain_OneRootRescueCompletes", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Voyage failedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("One failed chain")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);
                Mission rootFailure = await testDb.Driver.Missions.CreateAsync(new Mission("Root failure")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Downstream failure")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    DependsOnMissionId = rootFailure.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);
                Voyage rescueVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Root rescue")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Complete
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Completed root rescue")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = rescueVoyage.Id,
                    Description = RescueMissionMarker.Marker,
                    ParentMissionId = rootFailure.Id,
                    Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "One chain has one recovery obligation",
                    Status = ObjectiveStatusEnum.InProgress,
                    VoyageIds = new List<string> { failedVoyage.Id, rescueVoyage.Id }
                }).ConfigureAwait(false);

                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    EnabledSchedulerSettings());
                await scheduler.SweepAsync().ConfigureAwait(false);

                Objective? reconciled = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertEqual(ObjectiveStatusEnum.Completed, reconciled!.Status,
                    "Two failed missions on one dependency path form one chain rooted at the first failure.");
            }).ConfigureAwait(false);

            await RunTest("ReconcileObjective_GenericChildVoyage_DoesNotCountAsRescue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Voyage failedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Failed original")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);
                Mission failedMission = await testDb.Driver.Missions.CreateAsync(new Mission("Failed mission")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);
                Voyage childVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Ordinary child voyage")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Complete
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Ordinary child mission")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = childVoyage.Id,
                    ParentMissionId = failedMission.Id,
                    Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Generic child is not recovery evidence",
                    Status = ObjectiveStatusEnum.InProgress,
                    VoyageIds = new List<string> { failedVoyage.Id, childVoyage.Id }
                }).ConfigureAwait(false);

                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    EnabledSchedulerSettings());
                await scheduler.SweepAsync().ConfigureAwait(false);

                Objective? reconciled = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertEqual(ObjectiveStatusEnum.InProgress, reconciled!.Status,
                    "A generic cross-voyage parent link must not be treated as an automatic rescue.");
            }).ConfigureAwait(false);

            await RunTest("ReconcileObjective_CompleteVoyageWithCancelledMission_StaysInProgress", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Invalid complete voyage")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Complete
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Landed mission")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = voyage.Id,
                    Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Independent cancelled mission")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = voyage.Id,
                    Status = MissionStatusEnum.Cancelled
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Cancelled branch is unresolved",
                    Status = ObjectiveStatusEnum.InProgress,
                    VoyageIds = new List<string> { voyage.Id }
                }).ConfigureAwait(false);

                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    EnabledSchedulerSettings());
                await scheduler.SweepAsync().ConfigureAwait(false);

                Objective? reconciled = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertEqual(ObjectiveStatusEnum.InProgress, reconciled!.Status,
                    "A Complete voyage with an independent cancelled mission is not verified completion.");
            }).ConfigureAwait(false);

            await RunTest("ReconcileObjective_RescueWithCancelledVerification_StaysInProgress", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Voyage failedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Failed original")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);
                Mission failure = await testDb.Driver.Missions.CreateAsync(new Mission("Failed original mission")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);
                Voyage rescueVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Unverified rescue")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Complete
                }).ConfigureAwait(false);
                Mission rescue = await testDb.Driver.Missions.CreateAsync(new Mission("Rescue worker")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = rescueVoyage.Id,
                    Description = RescueMissionMarker.Marker,
                    ParentMissionId = failure.Id,
                    Status = MissionStatusEnum.WorkProduced
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Cancelled rescue judge")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = rescueVoyage.Id,
                    DependsOnMissionId = rescue.Id,
                    Status = MissionStatusEnum.Cancelled
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Unverified rescue does not close",
                    Status = ObjectiveStatusEnum.InProgress,
                    VoyageIds = new List<string> { failedVoyage.Id, rescueVoyage.Id }
                }).ConfigureAwait(false);

                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    EnabledSchedulerSettings());
                await scheduler.SweepAsync().ConfigureAwait(false);

                Objective? reconciled = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertEqual(ObjectiveStatusEnum.InProgress, reconciled!.Status,
                    "A rescue whose verification mission was cancelled must not count as recovered.");
            }).ConfigureAwait(false);

            await RunTest("ReconcileObjective_WorkProducedRescueWithCompletedJudge_StaysInProgress", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Voyage failedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Failed original")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);
                Mission failure = await testDb.Driver.Missions.CreateAsync(new Mission("Failed original mission")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);
                Voyage rescueVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Unlanded rescue")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Complete
                }).ConfigureAwait(false);
                Mission rescue = await testDb.Driver.Missions.CreateAsync(new Mission("Rescue worker")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = rescueVoyage.Id,
                    Description = RescueMissionMarker.Marker,
                    ParentMissionId = failure.Id,
                    Status = MissionStatusEnum.WorkProduced
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Completed rescue judge")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = rescueVoyage.Id,
                    DependsOnMissionId = rescue.Id,
                    Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Reviewed rescue has not landed",
                    Status = ObjectiveStatusEnum.InProgress,
                    VoyageIds = new List<string> { failedVoyage.Id, rescueVoyage.Id }
                }).ConfigureAwait(false);

                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    EnabledSchedulerSettings());
                await scheduler.SweepAsync().ConfigureAwait(false);

                Objective? reconciled = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertEqual(ObjectiveStatusEnum.InProgress, reconciled!.Status,
                    "A completed review must not close the objective while the rescue work is only WorkProduced.");
            }).ConfigureAwait(false);

            await RunTest("ReconcileObjective_MarkedChildOfCompletedMission_IsNotARescue", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Voyage completeVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Completed original")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Complete
                }).ConfigureAwait(false);
                Mission completedMission = await testDb.Driver.Missions.CreateAsync(new Mission("Completed original mission")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = completeVoyage.Id,
                    Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);
                Voyage failedChildVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Failed marked child")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Marked child mission")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedChildVoyage.Id,
                    Description = RescueMissionMarker.Marker,
                    ParentMissionId = completedMission.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Marked child does not hide failure",
                    Status = ObjectiveStatusEnum.InProgress,
                    VoyageIds = new List<string> { completeVoyage.Id, failedChildVoyage.Id }
                }).ConfigureAwait(false);

                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    EnabledSchedulerSettings());
                await scheduler.SweepAsync().ConfigureAwait(false);

                Objective? reconciled = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertEqual(ObjectiveStatusEnum.InProgress, reconciled!.Status,
                    "A marked child of a completed mission is not a recovery attempt and stays an objective obligation.");
            }).ConfigureAwait(false);

            await RunTest("ReconcileObjective_CompleteVoyageWithDanglingDependency_StaysInProgress", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Malformed complete voyage")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Complete
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Mission with missing dependency")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = voyage.Id,
                    DependsOnMissionId = "missing-mission",
                    Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Malformed graph stays open",
                    Status = ObjectiveStatusEnum.InProgress,
                    VoyageIds = new List<string> { voyage.Id }
                }).ConfigureAwait(false);

                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    EnabledSchedulerSettings());
                await scheduler.SweepAsync().ConfigureAwait(false);

                Objective? reconciled = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertEqual(ObjectiveStatusEnum.InProgress, reconciled!.Status,
                    "A dangling dependency must fail closeout closed even when the voyage status is Complete.");
            }).ConfigureAwait(false);

            await RunTest("ReconcileObjective_CompleteVoyageWithCompletedExternalDependency_Completes", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Mission external = await testDb.Driver.Missions.CreateAsync(new Mission("Completed external prerequisite")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Valid complete voyage")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Complete
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Completed dependent mission")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = voyage.Id,
                    DependsOnMissionId = external.Id,
                    Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "External prerequisite is valid",
                    Status = ObjectiveStatusEnum.InProgress,
                    VoyageIds = new List<string> { voyage.Id }
                }).ConfigureAwait(false);

                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver,
                    new RecordingAdmiralService(testDb.Driver),
                    EnabledSchedulerSettings());
                await scheduler.SweepAsync().ConfigureAwait(false);

                Objective? reconciled = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertEqual(ObjectiveStatusEnum.Completed, reconciled!.Status,
                    "A valid completed external dependency must not block objective closeout.");
            }).ConfigureAwait(false);

            await RunTest("ReconcileObjective_FailedOriginalWithNoRescue_StaysInProgress", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Voyage failedVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Original voyage")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Failed
                }).ConfigureAwait(false);

                await testDb.Driver.Missions.CreateAsync(new Mission("Original judge")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = failedVoyage.Id,
                    Status = MissionStatusEnum.Failed
                }).ConfigureAwait(false);

                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Failed original, never rescued",
                    Status = ObjectiveStatusEnum.InProgress,
                    VoyageIds = new List<string> { failedVoyage.Id }
                }).ConfigureAwait(false);

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        IntervalMinutes = 1,
                        MaxConcurrentVoyages = 3,
                        MaxConcurrentVoyagesPerVessel = 3
                    }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);
                await scheduler.SweepAsync().ConfigureAwait(false);

                Objective? still = await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false);
                AssertEqual(ObjectiveStatusEnum.InProgress, still!.Status,
                    "An objective whose failed voyage was never rescued must not reconcile to Completed.");
            }).ConfigureAwait(false);

            await RunTest("A scheduler-owned admission blocks an operator before it creates a voyage", async () =>
            {
                // Production failure: a scheduler sweep and an operator dispatch both read one
                // ReadyForDispatch objective with no active voyage, both created one, and both
                // linked - eight active voyages for four objectives. Both paths must settle on
                // one winner through the same atomic link guard.
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("race-vessel", "https://github.com/test/race.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Raced objective",
                    Status = ObjectiveStatusEnum.Scoped,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id }
                }).ConfigureAwait(false);

                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                ArmadaSettings settings = EnabledSchedulerSettings();

                RecordingAdmiralService schedulerAdmiral = new RecordingAdmiralService(testDb.Driver);
                TaskCompletionSource<bool> schedulerEnteredCreate = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<bool> releaseSchedulerCreate = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                schedulerAdmiral.BeforeVoyageCreateAsync = async () =>
                {
                    schedulerEnteredCreate.TrySetResult(true);
                    await releaseSchedulerCreate.Task.ConfigureAwait(false);
                };
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, schedulerAdmiral, settings);

                RecordingAdmiralService operatorAdmiral = new RecordingAdmiralService(testDb.Driver);
                VoyageDispatchService dispatchService = new VoyageDispatchService(
                    testDb.Driver,
                    operatorAdmiral,
                    objectiveService: objectives,
                    settings: new ArmadaSettings { CodeIndex = { Enabled = false } });

                Task schedulerTask = scheduler.SweepAsync();
                await schedulerEnteredCreate.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                string leaseName = ObjectiveService.BuildDispatchAdmissionLeaseName(
                    Constants.DefaultTenantId,
                    objective.Id);
                AssertNotNull(await testDb.Driver.CoordinationLeases.ReadAsync(leaseName).ConfigureAwait(false),
                    "The scheduler must hold the durable admission before Admiral creates the voyage.");
                AssertEqual(0, (await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false)).Count,
                    "The test gate must pause before voyage creation.");

                Task<VoyageDispatchResult> operatorTask = dispatchService.DispatchAsync(new SharedVoyageDispatchRequest
                {
                    Title = "Operator dispatch",
                    VesselId = vessel.Id,
                    ObjectiveId = objective.Id,
                    ObjectiveAuthContext = McpTestCaller.Operator,
                    Missions = new List<MissionDescription>
                    {
                        new MissionDescription("Implement", "Operator work for the raced objective.")
                    }
                });
                Task earlyCompletion = await Task.WhenAny(operatorTask, Task.Delay(100)).ConfigureAwait(false);
                AssertFalse(ReferenceEquals(earlyCompletion, operatorTask),
                    "The operator must wait while the scheduler owns durable admission.");
                AssertEqual(0, operatorAdmiral.DispatchVoyageCallCount,
                    "The waiting operator must not reach Admiral before admission settles.");
                releaseSchedulerCreate.TrySetResult(true);
                await Task.WhenAll(schedulerTask, operatorTask).ConfigureAwait(false);
                VoyageDispatchResult operatorResult = operatorTask.Result;

                Objective stored = (await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false))!;
                AssertEqual(1, stored.VoyageIds.Count,
                    "Exactly one voyage may be linked to the objective after the race.");
                Voyage winner = (await testDb.Driver.Voyages.ReadAsync(stored.VoyageIds[0]).ConfigureAwait(false))!;
                AssertTrue(ObjectiveService.IsActiveVoyageStatus(winner.Status),
                    "The winning voyage stays nonterminal; no duplicate active voyage may exist.");

                List<Voyage> allVoyages = await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false);
                AssertEqual(1, allVoyages.Count,
                    "The losing operator must be refused before it creates any voyage.");
                AssertEqual(1, schedulerAdmiral.DispatchVoyageCallCount);
                AssertEqual(0, operatorAdmiral.DispatchVoyageCallCount);
                AssertFalse(operatorResult.Succeeded);
                AssertEqual(409, operatorResult.StatusCode,
                    "The operator dispatch must surface the objective_already_dispatched conflict.");
                AssertContains("objective_already_dispatched", JsonSerializer.Serialize(operatorResult.Value));
                AssertContains(winner.Id, JsonSerializer.Serialize(operatorResult.Value),
                    "The conflict must identify the winning voyage.");
                AssertNull(await testDb.Driver.CoordinationLeases.ReadAsync(leaseName).ConfigureAwait(false),
                    "The winning scheduler must release admission after linking.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_BusyObjectiveAdmission_RecordsAdmissionBusyAndDoesNotDispatch", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("busy-admission-scheduler", "https://github.com/test/busy-admission-scheduler.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Admission held elsewhere",
                    Status = ObjectiveStatusEnum.Planned,
                    BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch,
                    AutoDispatchEnabled = true,
                    VesselIds = new List<string> { vessel.Id }
                }).ConfigureAwait(false);
                string leaseName = ObjectiveService.BuildDispatchAdmissionLeaseName(Constants.DefaultTenantId, objective.Id);
                AssertTrue(await testDb.Driver.CoordinationLeases.TryAcquireAsync(
                    leaseName, "operator-dispatch", TimeSpan.FromMinutes(1), Constants.DefaultTenantId).ConfigureAwait(false));

                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = new AutonomousObjectiveScheduler(
                    testDb.Driver,
                    new ObjectiveService(testDb.Driver, dispatchAdmissionWait: TimeSpan.FromMilliseconds(200)),
                    admiral,
                    new StubMergeQueueService(),
                    EnabledSchedulerSettings(),
                    logging);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertContains("admission_busy", scheduler.LastSkipReason ?? String.Empty,
                    "The sweep must name the busy admission as its skip reason.");
                AssertEqual(0, admiral.DispatchVoyageCallCount, "A busy admission must not dispatch.");
                AssertEqual(0, (await testDb.Driver.Voyages.EnumerateAsync().ConfigureAwait(false)).Count);
                List<ArmadaEvent> events = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_admission_busy").ConfigureAwait(false);
                AssertEqual(1, events.Count, "The sweep must record the busy skip as an event.");
                CoordinationLease lease = (await testDb.Driver.CoordinationLeases.ReadAsync(leaseName).ConfigureAwait(false))!;
                AssertEqual("operator-dispatch", lease.Holder, "The scheduler must not disturb the holder's lease.");
            }).ConfigureAwait(false);

            await RunTest("ReconcileObjective_LandedVoyage_CompletesAndMovesBacklogToInbox", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Voyage landed = await testDb.Driver.Voyages.CreateAsync(new Voyage("Landed voyage")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Status = VoyageStatusEnum.Complete
                }).ConfigureAwait(false);
                await testDb.Driver.Missions.CreateAsync(new Mission("Landed work")
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    VoyageId = landed.Id,
                    Status = MissionStatusEnum.Complete
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Landed objective",
                    Status = ObjectiveStatusEnum.InProgress,
                    BacklogState = ObjectiveBacklogStateEnum.Dispatched,
                    VoyageIds = new List<string> { landed.Id }
                }).ConfigureAwait(false);

                AutonomousObjectiveScheduler scheduler = CreateScheduler(
                    testDb.Driver, new RecordingAdmiralService(testDb.Driver), EnabledSchedulerSettings());
                await scheduler.SweepAsync().ConfigureAwait(false);

                Objective reconciled = (await testDb.Driver.Objectives.ReadAsync(objective.Id).ConfigureAwait(false))!;
                AssertEqual(ObjectiveStatusEnum.Completed, reconciled.Status);
                AssertEqual(ObjectiveBacklogStateEnum.Inbox, reconciled.BacklogState,
                    "Landing reconciliation must leave the dispatchable backlog in the same write as completion.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_TerminalReadyForDispatchRow_NeverSelectsWhileValidRowDispatches", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel("terminal-backlog-vessel", "https://github.com/test/terminal-backlog.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);
                Objective contradictory = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Completed yet ReadyForDispatch",
                    Status = ObjectiveStatusEnum.Completed,
                    BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch,
                    AutoDispatchEnabled = true,
                    Priority = ObjectivePriorityEnum.P0,
                    VesselIds = new List<string> { vessel.Id }
                }).ConfigureAwait(false);
                Objective valid = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    TenantId = Constants.DefaultTenantId,
                    UserId = Constants.DefaultUserId,
                    Title = "Planned and ReadyForDispatch",
                    Status = ObjectiveStatusEnum.Planned,
                    BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch,
                    AutoDispatchEnabled = true,
                    Priority = ObjectivePriorityEnum.P2,
                    VesselIds = new List<string> { vessel.Id }
                }).ConfigureAwait(false);

                List<Objective> candidates = AutonomousObjectiveSelector.SelectCandidates(
                    await testDb.Driver.Objectives.EnumerateAsync().ConfigureAwait(false));
                AssertFalse(candidates.Any(item => item.Id == contradictory.Id),
                    "A terminal objective is never a scheduler candidate, whatever its backlog state reads.");

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, EnabledSchedulerSettings());
                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(1, admiral.DispatchVoyageCallCount, "Only the valid active row dispatches.");
                Objective storedValid = (await testDb.Driver.Objectives.ReadAsync(valid.Id).ConfigureAwait(false))!;
                AssertEqual(1, storedValid.VoyageIds.Count);
                Objective storedContradictory = (await testDb.Driver.Objectives.ReadAsync(contradictory.Id).ConfigureAwait(false))!;
                AssertEqual(0, storedContradictory.VoyageIds.Count);

                ObjectiveService objectives = new ObjectiveService(testDb.Driver);
                EnumerationResult<Objective> ready = await objectives.EnumerateAsync(
                    AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "UnitTest"),
                    new ObjectiveQuery { BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch, PageSize = 100 }).ConfigureAwait(false);
                AssertFalse(ready.Objects.Any(item => item.Id == contradictory.Id),
                    "A completed objective never appears in the ReadyForDispatch backlog.");
            }).ConfigureAwait(false);
        }

        private static RecordingObjectiveDispatchPreview DependencyBlockedPreview(
            Objective candidate,
            Objective blocker,
            Vessel vessel)
        {
            return new RecordingObjectiveDispatchPreview
            {
                Result = new ObjectiveDispatchPreview
                {
                    ObjectiveId = candidate.Id,
                    VesselId = vessel.Id,
                    IsReady = false,
                    BlockingChains = new List<List<string>>
                    {
                        new List<string> { candidate.Id, blocker.Id }
                    },
                    Issues = new List<ObjectiveDispatchPreviewIssue>
                    {
                        new ObjectiveDispatchPreviewIssue
                        {
                            Code = "objective_dependencies_incomplete",
                            Area = "admission",
                            Severity = ReadinessSeverityEnum.Error,
                            Message = "Dependency incomplete."
                        }
                    }
                }
            };
        }

        private static AutonomousObjectiveScheduler CreateScheduler(
            DatabaseDriver database,
            IAdmiralService admiral,
            ArmadaSettings settings,
            DispatchHold? dispatchHold = null,
            IObjectiveDispatchPreviewService? objectiveDispatchPreview = null,
            TimeSpan? refillDebounceDelay = null,
            int maxCandidatesPerSweep = 100,
            TimeSpan? sweepTimeBudget = null)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;

            return new AutonomousObjectiveScheduler(
                database,
                new ObjectiveService(database),
                admiral,
                new StubMergeQueueService(),
                settings,
                logging,
                null,
                dispatchHold,
                objectiveDispatchPreview,
                refillDebounceDelay,
                maxCandidatesPerSweep,
                sweepTimeBudget);
        }

        private async Task WaitForEventTriggeredSweepCountAsync(
            AutonomousObjectiveScheduler scheduler,
            long expectedCount)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (scheduler.EventTriggeredSweepCount < expectedCount && DateTime.UtcNow < deadline)
                await Task.Delay(10).ConfigureAwait(false);

            AssertEqual(expectedCount, scheduler.EventTriggeredSweepCount,
                "The expected event-triggered sweep did not start before the test deadline.");
        }

        private async Task WaitForSweepCompletedAfterAsync(
            AutonomousObjectiveScheduler scheduler,
            DateTime? previousCompletionUtc)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while ((scheduler.LastSweepCompletedUtc == previousCompletionUtc || scheduler.SweepInProgress) && DateTime.UtcNow < deadline)
                await Task.Delay(10).ConfigureAwait(false);

            AssertFalse(scheduler.LastSweepCompletedUtc == previousCompletionUtc || scheduler.SweepInProgress,
                "The event-triggered sweep did not complete before the test deadline.");
        }

        private static ArmadaSettings EnabledSchedulerSettings()
        {
            return new ArmadaSettings
            {
                AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                {
                    Enabled = true,
                    IntervalMinutes = 1,
                    MaxConcurrentVoyages = 3,
                    MaxConcurrentVoyagesPerVessel = 3
                }
            };
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

        private sealed class RecordingObjectiveDispatchPreview : IObjectiveDispatchPreviewService
        {
            public ObjectiveDispatchPreview Result { get; set; } = new ObjectiveDispatchPreview { IsReady = true };
            public Func<Objective, CancellationToken, Task<ObjectiveDispatchPreview>>? Handler { get; set; }
            public int CallCount { get; private set; }

            public Task<ObjectiveDispatchPreview> PreviewAsync(
                AuthContext auth,
                Objective objective,
                string? requestedVesselId = null,
                string? requestedPipelineId = null,
                IReadOnlyList<CaptainAssignmentOverride>? captainAssignments = null,
                IReadOnlyList<MissionDescription>? missionDescriptions = null,
                CancellationToken token = default)
            {
                CallCount++;
                if (Handler != null)
                    return Handler(objective, token);
                return Task.FromResult(Result);
            }

        }

        private sealed class RecordingAdmiralService : IAdmiralService
        {
            private readonly DatabaseDriver _Database;

            public RecordingAdmiralService(DatabaseDriver database)
            {
                _Database = database;
            }

            public Exception? ThrowOnDispatch { get; set; }
            public Func<Task>? BeforeVoyageCreateAsync { get; set; }
            public int DispatchVoyageCallCount { get; private set; }
            public List<string> DispatchedTitles { get; } = new List<string>();

            public Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }
            public Func<Captain, Task>? OnStopAgent { get; set; }
            public Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }
            public Func<Mission, Dock, Task>? OnMissionComplete { get; set; }
            public Func<Voyage, Task>? OnVoyageComplete { get; set; }
            public Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }
            public Func<Task<int>>? OnReconcileMergeEntries { get; set; }
            public Func<int, bool>? OnIsProcessExitHandled { get; set; }
            public List<MissionDescription>? LastMissionDescriptions { get; set; }
            public StageSkipRequest? LastStageSkip { get; set; }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, StageSkipRequest? stageSkip, CancellationToken token = default)
            {
                LastStageSkip = stageSkip;
                return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, pipelineId, selectedPlaybooks, token);
            }

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
                => throw new NotImplementedException();

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, CancellationToken token = default)
                => throw new NotImplementedException();

            public async Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, string? pipelineId, List<SelectedPlaybook>? selectedPlaybooks, CancellationToken token = default)
            {
                if (ThrowOnDispatch != null) throw ThrowOnDispatch;
                DispatchVoyageCallCount++;
                DispatchedTitles.Add(title);
                LastMissionDescriptions = missionDescriptions;
                if (BeforeVoyageCreateAsync != null)
                    await BeforeVoyageCreateAsync().ConfigureAwait(false);
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
            public Task StopAllAgentProcessesAsync(CancellationToken token = default) => Task.CompletedTask;

            public Task HealthCheckAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task CleanupStaleCaptainsAsync(CancellationToken token = default)
                => Task.CompletedTask;

            public Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default)
                => Task.CompletedTask;
        }
    }
}
