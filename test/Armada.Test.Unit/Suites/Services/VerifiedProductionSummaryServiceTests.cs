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
                AssertEqual(1800000L, group.RescueRuntime.RescueMs);
                AssertEqual(5400000L, group.RescueRuntime.TotalMissionMs);
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
    }
}
