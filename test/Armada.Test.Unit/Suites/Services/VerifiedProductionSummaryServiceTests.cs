namespace Armada.Test.Unit.Suites.Services
{
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>Tests for verified production aggregation and coverage.</summary>
    public sealed class VerifiedProductionSummaryServiceTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Verified Production Summary Service";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("VerifiedSliceAndTimingAreCountedOnce", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage
                {
                    Title = "Delivery",
                    Status = VoyageStatusEnum.Complete,
                    CreatedUtc = start.AddHours(2),
                    CompletedUtc = start.AddHours(5)
                }).ConfigureAwait(false);
                Mission root = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    VoyageId = voyage.Id,
                    Title = "Implement",
                    Status = MissionStatusEnum.Complete,
                    CommitHash = "abc1234",
                    StartedUtc = start.AddHours(2),
                    CompletedUtc = start.AddHours(3),
                    TotalRuntimeMs = 3600000
                }).ConfigureAwait(false);
                Mission rescue = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    VoyageId = voyage.Id,
                    ParentMissionId = root.Id,
                    Title = "Rescue 1: Implement",
                    Status = MissionStatusEnum.Complete,
                    CommitHash = "def4567",
                    StartedUtc = start.AddHours(3),
                    CompletedUtc = start.AddHours(3).AddMinutes(30),
                    TotalRuntimeMs = 1800000
                }).ConfigureAwait(false);
                await testDb.Driver.MergeEntries.CreateAsync(new MergeEntry
                {
                    MissionId = rescue.Id,
                    BranchName = "work",
                    Status = MergeStatusEnum.Landed,
                    CompletedUtc = start.AddHours(4)
                }).ConfigureAwait(false);
                CheckRun check = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                {
                    MissionId = rescue.Id,
                    VoyageId = voyage.Id,
                    Status = CheckRunStatusEnum.Passed,
                    Command = "dotnet test",
                    CommitHash = "def4567",
                    CreatedUtc = start.AddHours(3),
                    StartedUtc = start.AddHours(3).AddMinutes(10),
                    CompletedUtc = start.AddHours(3).AddMinutes(12),
                    DurationMs = 120000
                }).ConfigureAwait(false);
                await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                {
                    MissionId = root.Id,
                    VoyageId = voyage.Id,
                    Status = CheckRunStatusEnum.Failed,
                    Command = "dotnet test",
                    CommitHash = "old1234",
                    CreatedUtc = start.AddHours(2)
                }).ConfigureAwait(false);
                Objective objective = await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "ECU protocol slice",
                    Status = ObjectiveStatusEnum.Completed,
                    BacklogState = ObjectiveBacklogStateEnum.Dispatched,
                    Category = "Protocol",
                    Tags = new List<string> { "port:ecu" },
                    VoyageIds = new List<string> { voyage.Id },
                    MissionIds = new List<string> { root.Id, rescue.Id },
                    CheckRunIds = new List<string> { check.Id },
                    CreatedUtc = start,
                    CompletedUtc = start.AddHours(5),
                    LastUpdateUtc = start.AddHours(5)
                }).ConfigureAwait(false);
                Objective ready = new Objective
                {
                    Id = objective.Id,
                    Title = objective.Title,
                    BacklogState = ObjectiveBacklogStateEnum.ReadyForDispatch,
                    LastUpdateUtc = start.AddHours(1)
                };
                await testDb.Driver.Events.CreateAsync(new ArmadaEvent
                {
                    EventType = "objective.snapshot",
                    EntityType = "objective",
                    EntityId = objective.Id,
                    Payload = JsonSerializer.Serialize(ready),
                    CreatedUtc = start.AddHours(1)
                }).ConfigureAwait(false);
                await testDb.Driver.Events.CreateAsync(new ArmadaEvent
                {
                    EventType = "check.auto_queued",
                    EntityType = "check_run",
                    EntityId = check.Id,
                    CreatedUtc = start.AddHours(3).AddMinutes(8)
                }).ConfigureAwait(false);

                VerifiedProductionSummaryService service = new VerifiedProductionSummaryService(testDb.Driver);
                ProductionSummaryResult result = await service.SummarizeAsync(
                    AuthContext.Authenticated("default", "default", true, true, "UnitTest"),
                    new ProductionSummaryQuery { FromUtc = start, ToUtc = start.AddDays(7) }).ConfigureAwait(false);

                ProductionSummaryGroup group = result.Groups.Single();
                AssertEqual("ecu", group.SourceFamily);
                AssertEqual("Protocol", group.WorkType);
                AssertEqual(1, group.VerifiedLandedSlices.Count);
                AssertEqual(1, result.RawCompletedSlices);
                AssertEqual(7, result.CompleteDayCount);
                AssertEqual(1, group.ByDay.Sum(item => item.Count));
                AssertEqual(3600000L, group.ReadyToDispatchDelayMs.P50!.Value);
                AssertEqual(600000L, group.CheckTiming.ArmedToStartMs.P50!.Value);
                AssertEqual("unavailable", group.CheckTiming.HostQueueMs.Availability);
                AssertTrue(group.CheckTiming.HostQueueMs.P50 == null);
                AssertEqual(120000L, group.CheckTiming.ExecutionMs.P50!.Value);
                AssertEqual(1, group.CheckTiming.ArmedToStartMs.Unknown);
                AssertEqual(1, group.CheckTiming.ExecutionMs.Unknown);
                AssertEqual(0L, group.RescueRuntime.RescueMs, "Runs without attempt facts are never classified as rescue by lineage or title");
                AssertEqual(0L, group.RescueRuntime.TotalMissionMs);
                AssertEqual(2, group.RescueRuntime.HistoricalUnclassifiedMissionCount);
                AssertEqual(5400000L, group.RescueRuntime.HistoricalUnclassifiedMs);
                AssertEqual("unavailable", group.RescueRuntime.Availability);
                AssertEqual(1, group.FirstPassAcceptance.Unknown);
                AssertEqual(1, group.FirstPassAcceptance.UnknownByReason["attempt_facts_not_recorded"]);
                AssertEqual(3600000L, group.LandedToVerifiedCloseoutMs.P50!.Value);
                AssertEqual("unavailable", group.PostLandRegressions.Availability);

                check.Status = CheckRunStatusEnum.Canceled;
                await testDb.Driver.CheckRuns.UpdateAsync(check).ConfigureAwait(false);
                ProductionSummaryResult canceledResult = await service.SummarizeAsync(
                    AuthContext.Authenticated("default", "default", true, true, "UnitTest"),
                    new ProductionSummaryQuery { FromUtc = start, ToUtc = start.AddDays(7) }).ConfigureAwait(false);
                AssertEqual(0, canceledResult.Groups.Single().VerifiedLandedSlices.Count);
                AssertEqual(1, canceledResult.Groups.Single().VerifiedLandedSlices.Unknown);
            }).ConfigureAwait(false);

            await RunTest("EveryIndependentDeliveryTipRequiresLandingAndChecks", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage
                {
                    Title = "Parallel delivery",
                    Status = VoyageStatusEnum.Complete,
                    CreatedUtc = start,
                    CompletedUtc = start.AddHours(2)
                }).ConfigureAwait(false);
                Mission first = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    VoyageId = voyage.Id,
                    Title = "First chain",
                    Status = MissionStatusEnum.Complete,
                    CommitHash = "aaaaaaa",
                    CompletedUtc = start.AddHours(1)
                }).ConfigureAwait(false);
                Mission second = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    VoyageId = voyage.Id,
                    Title = "Second chain",
                    Status = MissionStatusEnum.Complete,
                    CommitHash = "bbbbbbb",
                    CompletedUtc = start.AddHours(1)
                }).ConfigureAwait(false);
                await testDb.Driver.MergeEntries.CreateAsync(new MergeEntry
                {
                    MissionId = first.Id,
                    BranchName = "first",
                    Status = MergeStatusEnum.Landed,
                    CompletedUtc = start.AddHours(1)
                }).ConfigureAwait(false);
                await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                {
                    MissionId = first.Id,
                    VoyageId = voyage.Id,
                    Command = "dotnet test",
                    CommitHash = "aaaaaaa",
                    Status = CheckRunStatusEnum.Passed,
                    CompletedUtc = start.AddHours(1)
                }).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Two chains",
                    Status = ObjectiveStatusEnum.Completed,
                    VoyageIds = new List<string> { voyage.Id },
                    MissionIds = new List<string> { first.Id, second.Id },
                    CompletedUtc = start.AddHours(2),
                    LastUpdateUtc = start.AddHours(2)
                }).ConfigureAwait(false);

                ProductionSummaryResult result = await new VerifiedProductionSummaryService(testDb.Driver).SummarizeAsync(
                    AuthContext.Authenticated("default", "default", true, true, "UnitTest"),
                    new ProductionSummaryQuery { FromUtc = start, ToUtc = start.AddDays(1) }).ConfigureAwait(false);

                AssertEqual(0, result.Groups.Single().VerifiedLandedSlices.Count);
                AssertEqual(1, result.ExclusionsByReason["missing_landing_evidence"]);
            }).ConfigureAwait(false);

            await RunTest("UnresolvedExplicitMissionLinkCannotVerify", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage
                {
                    Title = "Delivery",
                    Status = VoyageStatusEnum.Complete,
                    CreatedUtc = start,
                    CompletedUtc = start.AddHours(1)
                }).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Missing mission",
                    Status = ObjectiveStatusEnum.Completed,
                    VoyageIds = new List<string> { voyage.Id },
                    MissionIds = new List<string> { "mis_missing" },
                    CompletedUtc = start.AddHours(1),
                    LastUpdateUtc = start.AddHours(1)
                }).ConfigureAwait(false);

                ProductionSummaryResult result = await new VerifiedProductionSummaryService(testDb.Driver).SummarizeAsync(
                    AuthContext.Authenticated("default", "default", true, true, "UnitTest"),
                    new ProductionSummaryQuery { FromUtc = start, ToUtc = start.AddDays(1) }).ConfigureAwait(false);

                AssertEqual(0, result.Groups.Single().VerifiedLandedSlices.Count);
                AssertEqual(1, result.ExclusionsByReason["missing_linked_mission"]);
            }).ConfigureAwait(false);

            await RunTest("MissingEvidenceIsUnknownNotZero", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime start = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Uncovered slice",
                    Status = ObjectiveStatusEnum.Completed,
                    CompletedUtc = start.AddDays(1),
                    LastUpdateUtc = start.AddDays(1)
                }).ConfigureAwait(false);

                ProductionSummaryResult result = await new VerifiedProductionSummaryService(testDb.Driver).SummarizeAsync(
                    AuthContext.Authenticated("default", "default", true, true, "UnitTest"),
                    new ProductionSummaryQuery { FromUtc = start, ToUtc = start.AddDays(7) }).ConfigureAwait(false);

                ProductionSummaryGroup group = result.Groups.Single();
                AssertEqual(0, group.VerifiedLandedSlices.Count);
                AssertEqual(1, group.VerifiedLandedSlices.Unknown);
                AssertEqual("partial", group.VerifiedLandedSlices.Availability);
                AssertEqual(1, result.ExclusionsByReason["missing_voyage_link"]);
            }).ConfigureAwait(false);

            await RunTest("FirstPassAcceptanceUsesDurableAttemptFacts", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
                Mission clean = await CreateVerifiedSliceAsync(testDb, start, "clean").ConfigureAwait(false);
                Mission denied = await CreateVerifiedSliceAsync(testDb, start, "denied").ConfigureAwait(false);
                Mission waited = await CreateVerifiedSliceAsync(testDb, start, "waited").ConfigureAwait(false);
                await CreateVerifiedSliceAsync(testDb, start, "historical").ConfigureAwait(false);

                await AddFactAsync(testDb, clean, MissionAttemptFactTypeEnum.AttemptStarted, null, start).ConfigureAwait(false);
                await AddFactAsync(testDb, clean, MissionAttemptFactTypeEnum.Landed, "landed", start).ConfigureAwait(false);
                await AddFactAsync(testDb, denied, MissionAttemptFactTypeEnum.AttemptStarted, null, start).ConfigureAwait(false);
                await AddFactAsync(testDb, denied, MissionAttemptFactTypeEnum.ReviewDenied, "review_denied_retrystage", start).ConfigureAwait(false);
                await AddFactAsync(testDb, denied, MissionAttemptFactTypeEnum.AttemptStarted, null, start).ConfigureAwait(false);
                await AddFactAsync(testDb, waited, MissionAttemptFactTypeEnum.AttemptStarted, null, start).ConfigureAwait(false);
                await AddFactAsync(testDb, waited, MissionAttemptFactTypeEnum.Retried, MissionAttemptFactRules.JudgeCheckWaitReason, start).ConfigureAwait(false);
                await AddFactAsync(testDb, waited, MissionAttemptFactTypeEnum.AttemptStarted, null, start).ConfigureAwait(false);

                ProductionSummaryResult result = await new VerifiedProductionSummaryService(testDb.Driver).SummarizeAsync(
                    AuthContext.Authenticated("default", "default", true, true, "UnitTest"),
                    new ProductionSummaryQuery { FromUtc = start, ToUtc = start.AddDays(7) }).ConfigureAwait(false);

                ProductionSummaryGroup group = result.Groups.Single();
                AssertEqual(4, group.VerifiedLandedSlices.Count);
                AssertEqual(3, group.FirstPassAcceptance.Eligible, "Slices whose every run has attempt facts are eligible");
                AssertEqual(2, group.FirstPassAcceptance.Accepted, "A review denial ends first pass; a Check-wait re-run does not");
                AssertEqual(1, group.FirstPassAcceptance.Unknown, "A slice that ran before facts were recorded is explicit history");
                AssertEqual("partial", group.FirstPassAcceptance.Availability);
                AssertTrue(group.FirstPassAcceptance.Rate.HasValue && Math.Abs(group.FirstPassAcceptance.Rate.Value - (2.0 / 3.0)) < 0.0001);
            }).ConfigureAwait(false);

            await RunTest("RescueShareUsesTypedMarkerAcrossTheWholeChain", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // The recorder stamps facts with the current time, so the window must contain now.
                DateTime start = DateTime.UtcNow.AddHours(-6);
                Voyage original = await testDb.Driver.Voyages.CreateAsync(new Voyage { Title = "Original", Status = VoyageStatusEnum.Failed, CreatedUtc = start }).ConfigureAwait(false);
                Voyage rescueVoyage = await testDb.Driver.Voyages.CreateAsync(new Voyage { Title = "Recovery", Status = VoyageStatusEnum.Complete, CreatedUtc = start.AddHours(2) }).ConfigureAwait(false);
                Mission root = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    VoyageId = original.Id, Title = "Implement", Status = MissionStatusEnum.Failed,
                    StartedUtc = start, CompletedUtc = start.AddMinutes(60)
                }).ConfigureAwait(false);
                Mission titleOnly = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    VoyageId = original.Id, Title = "Rescue: looks like recovery but is not", Status = MissionStatusEnum.Complete,
                    StartedUtc = start, CompletedUtc = start.AddMinutes(20)
                }).ConfigureAwait(false);
                Mission rescue = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    VoyageId = rescueVoyage.Id, ParentMissionId = root.Id, Title = "Revision",
                    Description = RescueMissionMarker.Marker + "\nRevise the work.", Status = MissionStatusEnum.Complete,
                    StartedUtc = start.AddHours(2), CompletedUtc = start.AddHours(2).AddMinutes(30)
                }).ConfigureAwait(false);
                Mission review = await testDb.Driver.Missions.CreateAsync(new Mission
                {
                    VoyageId = rescueVoyage.Id, DependsOnMissionId = rescue.Id, Title = "Review revision",
                    Description = RescueMissionMarker.Marker + "\nReview the revision.", Status = MissionStatusEnum.Complete,
                    StartedUtc = start.AddHours(3), CompletedUtc = start.AddHours(3).AddMinutes(10)
                }).ConfigureAwait(false);
                await testDb.Driver.Objectives.CreateAsync(new Objective
                {
                    Title = "Recovered slice",
                    Status = ObjectiveStatusEnum.Completed,
                    VoyageIds = new List<string> { original.Id },
                    MissionIds = new List<string> { root.Id, titleOnly.Id },
                    CompletedUtc = start.AddHours(4),
                    LastUpdateUtc = start.AddHours(4)
                }).ConfigureAwait(false);
                foreach (Mission mission in new[] { root, titleOnly, rescue, review })
                    await MissionAttemptFactRecorder.RecordAsync(testDb.Driver, mission, MissionAttemptFactTypeEnum.AttemptStarted).ConfigureAwait(false);

                ProductionSummaryResult result = await new VerifiedProductionSummaryService(testDb.Driver).SummarizeAsync(
                    AuthContext.Authenticated("default", "default", true, true, "UnitTest"),
                    new ProductionSummaryQuery { FromUtc = start, ToUtc = DateTime.UtcNow.AddMinutes(10) }).ConfigureAwait(false);

                ProductionRescueRuntimeMetric rescueRuntime = result.Groups.Single().RescueRuntime;
                AssertEqual(40L * 60000L, rescueRuntime.RescueMs, "The marked rescue and its chained review stage are rescue runtime");
                AssertEqual(120L * 60000L, rescueRuntime.TotalMissionMs, "The unlinked recovery voyage is part of the chain");
                AssertEqual(0.0 + (40.0 / 120.0), rescueRuntime.Share!.Value);
            }).ConfigureAwait(false);

            await RunTest("WindowOverNinetyDaysIsRejected", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                bool threw = false;
                try
                {
                    await new VerifiedProductionSummaryService(testDb.Driver).SummarizeAsync(
                        AuthContext.Authenticated("default", "default", true, true, "UnitTest"),
                        new ProductionSummaryQuery { FromUtc = start, ToUtc = start.AddDays(91) }).ConfigureAwait(false);
                }
                catch (ArgumentException) { threw = true; }
                AssertTrue(threw, "A report window longer than 90 days must be rejected.");
            }).ConfigureAwait(false);
        }

        private static async Task<Mission> CreateVerifiedSliceAsync(TestDatabase testDb, DateTime start, string label)
        {
            string commit = (label + "0000000").Substring(0, 7);
            Voyage voyage = await testDb.Driver.Voyages.CreateAsync(new Voyage
            {
                Title = label,
                Status = VoyageStatusEnum.Complete,
                CreatedUtc = start,
                CompletedUtc = start.AddHours(1)
            }).ConfigureAwait(false);
            Mission mission = await testDb.Driver.Missions.CreateAsync(new Mission
            {
                VoyageId = voyage.Id,
                Title = label,
                Status = MissionStatusEnum.Complete,
                CommitHash = commit,
                StartedUtc = start,
                CompletedUtc = start.AddMinutes(30)
            }).ConfigureAwait(false);
            await testDb.Driver.MergeEntries.CreateAsync(new MergeEntry
            {
                MissionId = mission.Id,
                BranchName = label,
                Status = MergeStatusEnum.Landed,
                CompletedUtc = start.AddMinutes(40)
            }).ConfigureAwait(false);
            await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
            {
                MissionId = mission.Id,
                VoyageId = voyage.Id,
                Status = CheckRunStatusEnum.Passed,
                Command = "dotnet test",
                CommitHash = commit,
                CreatedUtc = start.AddMinutes(31),
                StartedUtc = start.AddMinutes(32),
                CompletedUtc = start.AddMinutes(35),
                DurationMs = 180000
            }).ConfigureAwait(false);
            await testDb.Driver.Objectives.CreateAsync(new Objective
            {
                Title = label,
                Status = ObjectiveStatusEnum.Completed,
                VoyageIds = new List<string> { voyage.Id },
                MissionIds = new List<string> { mission.Id },
                CompletedUtc = start.AddHours(1),
                LastUpdateUtc = start.AddHours(1)
            }).ConfigureAwait(false);
            return mission;
        }

        private static async Task AddFactAsync(TestDatabase testDb, Mission mission, MissionAttemptFactTypeEnum type, string? reason, DateTime createdUtc)
        {
            await testDb.Driver.MissionAttemptFacts.CreateAsync(new MissionAttemptFact
            {
                TenantId = mission.TenantId,
                UserId = mission.UserId,
                MissionId = mission.Id,
                VoyageId = mission.VoyageId,
                RootMissionId = mission.Id,
                FactType = type,
                ReasonCode = reason,
                CreatedUtc = createdUtc
            }).ConfigureAwait(false);
        }
    }
}
