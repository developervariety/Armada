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

            await RunTest("SweepAsync_DependencyDiagnosticsPrecedeZeroGlobalCapacity", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "dependency-zero-capacity", "https://github.com/test/dependency-zero-capacity.git")).ConfigureAwait(false);
                Voyage activeVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage("Active capacity consumer")
                {
                    Status = VoyageStatusEnum.InProgress
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

                AssertEqual("dependency_blocked=1", scheduler.LastSkipReason,
                    "A dependency diagnosis must not be hidden by zero global capacity.");
                AssertEqual(1, preview.CallCount, "The blocked candidate is previewed once.");
                List<ArmadaEvent> events = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_dependency")
                    .ConfigureAwait(false);
                AssertEqual(1, events.Count, "The complete dependency diagnostic is emitted before the capacity return.");
            }).ConfigureAwait(false);

            await RunTest("SweepAsync_DependencyDiagnosticsPrecedeBusySiblingLane", async () =>
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

                AssertEqual("dependency_blocked=1", scheduler.LastSkipReason,
                    "A dependency diagnosis must not be replaced by lane_busy.");
                AssertEqual(1, preview.CallCount, "The blocked lane candidate is previewed once.");
                List<ArmadaEvent> dependencyEvents = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_dependency")
                    .ConfigureAwait(false);
                AssertEqual(1, dependencyEvents.Count);
                List<ArmadaEvent> laneEvents = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_lane_busy")
                    .ConfigureAwait(false);
                AssertEqual(0, laneEvents.Count, "The lane gate must not hide or replace the dependency diagnostic.");
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

                    scheduler.RequestRefill();
                    scheduler.RequestRefill();
                    scheduler.RequestRefill();

                    await WaitForEventTriggeredSweepCountAsync(scheduler, 1).ConfigureAwait(false);
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

            await RunTest("Per-vessel saturation backfills remaining fleet capacity", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel primary = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "saturation-primary", "https://github.com/test/saturation-primary.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);

                for (int i = 0; i < 4; i++)
                {
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Title = "Primary priority " + i,
                        Status = ObjectiveStatusEnum.Scoped,
                        AutoDispatchEnabled = true,
                        Priority = ObjectivePriorityEnum.P0,
                        Rank = i,
                        VesselIds = new List<string> { primary.Id }
                    }).ConfigureAwait(false);
                }

                for (int i = 0; i < 4; i++)
                {
                    Vessel other = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                        "saturation-other-" + i, "https://github.com/test/saturation-other-" + i + ".git")
                    {
                        TenantId = Constants.DefaultTenantId
                    }).ConfigureAwait(false);
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Title = "Backfill priority " + i,
                        Status = ObjectiveStatusEnum.Scoped,
                        AutoDispatchEnabled = true,
                        Priority = ObjectivePriorityEnum.P1,
                        Rank = i,
                        VesselIds = new List<string> { other.Id }
                    }).ConfigureAwait(false);
                }

                ArmadaSettings settings = new ArmadaSettings
                {
                    AutonomousObjectiveScheduler = new AutonomousObjectiveSchedulerSettings
                    {
                        Enabled = true,
                        IntervalMinutes = 1,
                        MaxConcurrentVoyages = 7,
                        MaxConcurrentVoyagesPerVessel = 3
                    }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(7, admiral.DispatchVoyageCallCount,
                    "One sweep must backfill all fleet slots after the primary vessel reaches its cap.");
                AssertEqual("Primary priority 0", admiral.DispatchedTitles[0],
                    "Priority must remain authoritative for the first admissible objective.");
                AssertEqual("Primary priority 1", admiral.DispatchedTitles[1],
                    "Priority and rank must remain authoritative within the primary vessel.");
                AssertEqual("Primary priority 2", admiral.DispatchedTitles[2],
                    "The primary vessel may consume only its three admissible slots.");
                AssertEqual("Backfill priority 0", admiral.DispatchedTitles[3],
                    "The first admissible objective after saturation must backfill capacity.");
                for (int i = 0; i < 4; i++)
                    AssertEqual("Backfill priority " + i, admiral.DispatchedTitles[3 + i],
                        "Backfill objectives must retain priority and rank order.");
                AssertContains("vessel_concurrency=1", scheduler.LastResultSummary ?? String.Empty,
                    "The summary must report the saturated candidate that was skipped.");
                AssertContains("search_exhaustive=true", scheduler.LastResultSummary ?? String.Empty,
                    "The summary must report that every candidate was examined while backfilling capacity.");
                List<ArmadaEvent> saturationEvents = await testDb.Driver.Events
                    .EnumerateByTypeAsync("objective_scheduler.skipped_vessel_concurrency")
                    .ConfigureAwait(false);
                AssertEqual(1, saturationEvents.Count,
                    "The saturated candidate must be recorded with its skip reason.");
            }).ConfigureAwait(false);

            await RunTest("Fleet capacity reports a non-exhaustive candidate search", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);

                Vessel vessel = await testDb.Driver.Vessels.CreateAsync(new Vessel(
                    "non-exhaustive-vessel", "https://github.com/test/non-exhaustive-vessel.git")
                {
                    TenantId = Constants.DefaultTenantId
                }).ConfigureAwait(false);

                for (int i = 0; i < 4; i++)
                {
                    await testDb.Driver.Objectives.CreateAsync(new Objective
                    {
                        TenantId = Constants.DefaultTenantId,
                        UserId = Constants.DefaultUserId,
                        Title = "Capacity objective " + i,
                        Status = ObjectiveStatusEnum.Scoped,
                        AutoDispatchEnabled = true,
                        Priority = ObjectivePriorityEnum.P0,
                        Rank = i,
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
                        MaxConcurrentVoyagesPerVessel = 3
                    }
                };

                RecordingAdmiralService admiral = new RecordingAdmiralService(testDb.Driver);
                AutonomousObjectiveScheduler scheduler = CreateScheduler(testDb.Driver, admiral, settings);

                await scheduler.SweepAsync().ConfigureAwait(false);

                AssertEqual(3, admiral.DispatchVoyageCallCount,
                    "The sweep must stop after filling the fleet capacity.");
                AssertEqual("Capacity objective 2", admiral.DispatchedTitles[2],
                    "Priority rank must decide the final admitted objective.");
                AssertContains("search_exhaustive=false", scheduler.LastResultSummary ?? String.Empty,
                    "The summary must report that an eligible candidate remained unexamined.");
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
            TimeSpan? refillDebounceDelay = null)
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
                refillDebounceDelay);
        }

        private async Task WaitForEventTriggeredSweepCountAsync(
            AutonomousObjectiveScheduler scheduler,
            long expectedCount)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (scheduler.EventTriggeredSweepCount < expectedCount && DateTime.UtcNow < deadline)
                await Task.Delay(10).ConfigureAwait(false);

            AssertEqual(expectedCount, scheduler.EventTriggeredSweepCount,
                "The expected event-triggered sweep did not finish before the test deadline.");
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

            public Task<Voyage> DispatchVoyageAsync(string title, string description, string vesselId, List<MissionDescription> missionDescriptions, CancellationToken token = default)
                => throw new NotImplementedException();

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
