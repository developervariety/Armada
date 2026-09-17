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
                    Title = "Example protocol slice",
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
                AssertEqual("available", group.PostLandRegressions.Availability, "No typed regression record is linked to the verified slice");
                AssertEqual(0, group.PostLandRegressions.Consumer!.Value);
                AssertEqual(1, group.PostLandRegressions.VerifiedSlices);

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

            await RunTest("PostLandRegressionRatesAreSeparatedWithCoverage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime start = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
                await CreateVerifiedSliceAsync(testDb, start, "alpha").ConfigureAwait(false);
                await CreateVerifiedSliceAsync(testDb, start, "bravo").ConfigureAwait(false);
                await CreateVerifiedSliceAsync(testDb, start, "charlie").ConfigureAwait(false);
                Objective alpha = await ObjectiveTitledAsync(testDb, "alpha").ConfigureAwait(false);
                Objective bravo = await ObjectiveTitledAsync(testDb, "bravo").ConfigureAwait(false);
                Objective charlie = await ObjectiveTitledAsync(testDb, "charlie").ConfigureAwait(false);
                IncidentService incidents = new IncidentService(testDb.Driver);
                AuthContext auth = AuthContext.Authenticated("default", "default", true, true, "UnitTest");

                CheckRun consumerCheck = await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                {
                    Status = CheckRunStatusEnum.Failed, Command = "dotnet build consumer", CreatedUtc = start.AddHours(2),
                    RegressionPurpose = RegressionPurposeEnum.Consumer, RegressionObjectiveId = alpha.Id
                }).ConfigureAwait(false);
                await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                {
                    Status = CheckRunStatusEnum.Failed, Command = "dotnet test consumer", CreatedUtc = start.AddHours(2),
                    RegressionPurpose = RegressionPurposeEnum.Consumer, RegressionObjectiveId = bravo.Id
                }).ConfigureAwait(false);
                await CreateRegressionIncidentAsync(incidents, auth, start, "consumer caused", RegressionPurposeEnum.Consumer, RegressionCauseEnum.LandedChange, alpha.Id, CommitFor("alpha"), consumerCheck.Id).ConfigureAwait(false);
                await CreateRegressionIncidentAsync(incidents, auth, start, "ledger caused", RegressionPurposeEnum.Ledger, RegressionCauseEnum.LandedChange, bravo.Id, CommitFor("bravo"), null).ConfigureAwait(false);
                await CreateRegressionIncidentAsync(incidents, auth, start, "unclassified", RegressionPurposeEnum.Consumer, RegressionCauseEnum.Unclassified, charlie.Id, null, null).ConfigureAwait(false);
                await CreateRegressionIncidentAsync(incidents, auth, start, "unlinked ledger", RegressionPurposeEnum.Ledger, RegressionCauseEnum.LandedChange, null, null, null).ConfigureAwait(false);
                await CreateRegressionIncidentAsync(incidents, auth, start, "environment", RegressionPurposeEnum.Consumer, RegressionCauseEnum.Environment, charlie.Id, null, null).ConfigureAwait(false);
                await CreateRegressionIncidentAsync(incidents, auth, start, "wrong commit", RegressionPurposeEnum.Consumer, RegressionCauseEnum.LandedChange, charlie.Id, "deadbeef00", null).ConfigureAwait(false);

                ProductionSummaryResult result = await new VerifiedProductionSummaryService(testDb.Driver).SummarizeAsync(
                    auth, new ProductionSummaryQuery { FromUtc = start, ToUtc = start.AddDays(7) }).ConfigureAwait(false);

                ProductionRegressionMetric regressions = result.Groups.Single().PostLandRegressions;
                AssertEqual(3, regressions.VerifiedSlices);
                AssertEqual(1, regressions.Consumer, "A classified consumer incident supersedes its linked failed Check");
                AssertEqual(1, regressions.Ledger);
                AssertEqual(2, regressions.AffectedSlices);
                AssertTrue(regressions.ConsumerRate.HasValue && Math.Abs(regressions.ConsumerRate.Value - (1.0 / 3.0)) < 0.0001);
                AssertTrue(regressions.LedgerRate.HasValue && Math.Abs(regressions.LedgerRate.Value - (1.0 / 3.0)) < 0.0001);
                AssertEqual(2, regressions.UnknownByReason["cause_unclassified"], "An unclassified incident and an unexplained failed Check are unknown");
                AssertEqual(1, regressions.UnknownByReason["landed_commit_mismatch"], "A commit that is not a delivered tip of the slice is not attributed");
                AssertEqual(3, regressions.Unknown);
                AssertEqual("partial", regressions.Availability);
                AssertEqual(1, result.RegressionCoverage.Unlinked, "A landed-change regression without an objective link is unlinked");
                AssertEqual(1, result.RegressionCoverage.NotRegression, "An environment cause is excluded");
                AssertEqual(7, result.RegressionCoverage.RecordsRead, "The Check classified by an incident is one record, not two");
            }).ConfigureAwait(false);

            await RunTest("RepeatedResearchCountsOnlyReestablishedClaimsWithCoverage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
                await CreateVerifiedSliceAsync(testDb, start, "alpha").ConfigureAwait(false);
                await CreateVerifiedSliceAsync(testDb, start, "bravo").ConfigureAwait(false);
                await CreateVerifiedSliceAsync(testDb, start, "charlie").ConfigureAwait(false);
                Objective alpha = await ObjectiveTitledAsync(testDb, "alpha").ConfigureAwait(false);
                Objective bravo = await ObjectiveTitledAsync(testDb, "bravo").ConfigureAwait(false);
                await AddObservationAsync(testDb, alpha, "opc_a1", PreparationClaimObservationEnum.Established, start).ConfigureAwait(false);
                await AddObservationAsync(testDb, alpha, "opc_a1", PreparationClaimObservationEnum.Reestablished, start).ConfigureAwait(false);
                await AddObservationAsync(testDb, alpha, "opc_a1", PreparationClaimObservationEnum.Reestablished, start).ConfigureAwait(false);
                await AddObservationAsync(testDb, alpha, "opc_a1", PreparationClaimObservationEnum.Reused, start).ConfigureAwait(false);
                await AddObservationAsync(testDb, alpha, "opc_a2", PreparationClaimObservationEnum.Revalidated, start).ConfigureAwait(false);
                await AddObservationAsync(testDb, bravo, "opc_b1", PreparationClaimObservationEnum.Established, start).ConfigureAwait(false);
                await AddObservationAsync(testDb, bravo, "opc_b1", PreparationClaimObservationEnum.Reused, start).ConfigureAwait(false);

                ProductionSummaryResult result = await new VerifiedProductionSummaryService(testDb.Driver).SummarizeAsync(
                    AuthContext.Authenticated("default", "default", true, true, "UnitTest"),
                    new ProductionSummaryQuery { FromUtc = start, ToUtc = start.AddDays(7) }).ConfigureAwait(false);

                ProductionRepeatedResearchMetric research = result.Groups.Single().RepeatedResearch;
                AssertEqual(3, research.Slices);
                AssertEqual(2, research.CoveredSlices);
                AssertEqual(1, research.RepeatedClaims!.Value, "Only the re-established claim is repeated research");
                AssertEqual(2, research.ReestablishedObservations);
                AssertEqual(1, research.AffectedSlices!.Value);
                AssertEqual(1, research.RevalidatedClaims, "Stale-claim revalidation is separate from repetition");
                AssertEqual(2, research.ReusedClaims);
                AssertEqual(2, research.EstablishedClaims);
                AssertEqual(1, research.Unknown);
                AssertEqual(1, research.UnknownByReason["no_preparation_claims_recorded"]);
                AssertEqual("partial", research.Availability);
                AssertTrue(research.RepeatedMinutes == null, "Research duration is not recorded");
                ProductionClaimObservationCounts family = result.ClaimObservationsBySourceFamily["unknown"];
                AssertEqual(2, family.Established);
                AssertEqual(2, family.Reestablished);
                AssertEqual(1, family.Revalidated);
                AssertEqual(2, family.Reused);
            }).ConfigureAwait(false);

            await RunTest("HostSlotWaitIsSeparateFromPreparationAndExecution", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime start = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
                Mission mission = await CreateVerifiedSliceAsync(testDb, start, "slot").ConfigureAwait(false);
                await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                {
                    MissionId = mission.Id,
                    VoyageId = mission.VoyageId,
                    Status = CheckRunStatusEnum.Passed,
                    Command = "dotnet test",
                    CommitHash = CommitFor("slot"),
                    CreatedUtc = start.AddMinutes(31),
                    SlotRequestedUtc = start.AddMinutes(36),
                    StartedUtc = start.AddMinutes(38),
                    CompletedUtc = start.AddMinutes(39),
                    DurationMs = 60000
                }).ConfigureAwait(false);
                await testDb.Driver.CheckRuns.CreateAsync(new CheckRun
                {
                    MissionId = mission.Id,
                    VoyageId = mission.VoyageId,
                    Source = CheckRunSourceEnum.External,
                    Status = CheckRunStatusEnum.Passed,
                    Command = "external",
                    CommitHash = CommitFor("slot"),
                    CreatedUtc = start.AddMinutes(31),
                    StartedUtc = start.AddMinutes(32),
                    DurationMs = 1000
                }).ConfigureAwait(false);

                ProductionSummaryResult result = await new VerifiedProductionSummaryService(testDb.Driver).SummarizeAsync(
                    AuthContext.Authenticated("default", "default", true, true, "UnitTest"),
                    new ProductionSummaryQuery { FromUtc = start, ToUtc = start.AddDays(7) }).ConfigureAwait(false);

                ProductionCheckTimingMetric timing = result.Groups.Single().CheckTiming;
                AssertEqual(120000L, timing.HostQueueMs.P50!.Value, "host-slot wait runs from the slot request to start");
                AssertEqual(1, timing.HostQueueMs.Observed);
                AssertEqual(1, timing.HostQueueMs.Unknown, "an Armada Check started without a recorded slot request is unknown");
                AssertEqual("partial", timing.HostQueueMs.Availability);
                AssertEqual(300000L, timing.PreparationDelayMs.P50!.Value, "preparation runs from creation to the slot request");
                AssertEqual(1, timing.PreparationDelayMs.Observed);
                AssertEqual(3, timing.ExecutionMs.Observed, "execution still covers every timed Check");
            }).ConfigureAwait(false);

            await RunTest("EligibleIdleLaneMinutesUseObservedIntervalsWithCoverage", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime start = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
                await CreateVerifiedSliceAsync(testDb, start, "lane").ConfigureAwait(false);
                Objective laneObjective = await ObjectiveTitledAsync(testDb, "lane").ConfigureAwait(false);
                laneObjective.Tags = new List<string> { "port:ecu" };
                await testDb.Driver.Objectives.UpdateAsync(laneObjective).ConfigureAwait(false);

                await AddLaneRowAsync(testDb, "vsl_a", start.AddMinutes(-10), 1, 0, 1, LaneBlockReasonEnum.None, "ecu", 1800).ConfigureAwait(false);
                await AddLaneRowAsync(testDb, "vsl_a", start.AddMinutes(30), 1, 0, 1, LaneBlockReasonEnum.None, "ecu", 3600).ConfigureAwait(false);
                await AddLaneRowAsync(testDb, "vsl_a", start.AddMinutes(90), 1, 1, 1, LaneBlockReasonEnum.None, "ecu", 3600).ConfigureAwait(false);
                await AddLaneRowAsync(testDb, "vsl_b+vsl_c", start.AddMinutes(60), 2, 0, 1, LaneBlockReasonEnum.FleetCapacity, "alpha", 7200).ConfigureAwait(false);

                ProductionSummaryResult result = await new VerifiedProductionSummaryService(testDb.Driver).SummarizeAsync(
                    AuthContext.Authenticated("default", "default", true, true, "UnitTest"),
                    new ProductionSummaryQuery { FromUtc = start, ToUtc = start.AddHours(2) }).ConfigureAwait(false);

                ProductionLaneTimeSummary lanes = result.LaneTime;
                AssertEqual(2, lanes.Lanes);
                AssertEqual(240.0, lanes.ExpectedLaneMinutes);
                AssertEqual(170.0, lanes.ObservedLaneMinutes, "observations count only inside their trust windows");
                AssertEqual(70.0, lanes.UnobservedLaneMinutes);
                AssertEqual(2, lanes.IncompleteIntervals, "a trust-window gap and a lane first seen mid-window are incomplete");
                AssertEqual(80.0, lanes.EligibleIdleMinutes);
                AssertEqual(60.0, lanes.FleetBlockedMinutes, "fleet-capacity time is not lane idleness");
                AssertEqual("partial", lanes.Availability);
                ProductionIdleLaneMetric group = result.Groups.Single().EligibleIdleLaneMinutes;
                AssertEqual(80L, group.ObservedMinutes!.Value, "idle minutes are attributed by eligible source family");
                AssertEqual("partial", group.Availability);
            }).ConfigureAwait(false);

            await RunTest("WindowOlderThanFactRetentionReportsUnobservedNotWrong", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                DateTime start = DateTime.UtcNow.Date.AddDays(-400);
                Mission denied = await CreateVerifiedSliceAsync(testDb, start, "expired").ConfigureAwait(false);
                Objective objective = await ObjectiveTitledAsync(testDb, "expired").ConfigureAwait(false);
                await AddFactAsync(testDb, denied, MissionAttemptFactTypeEnum.AttemptStarted, null, start).ConfigureAwait(false);
                await AddFactAsync(testDb, denied, MissionAttemptFactTypeEnum.ReviewDenied, "review_denied_retrystage", start).ConfigureAwait(false);
                await AddObservationAsync(testDb, objective, "opc_expired", PreparationClaimObservationEnum.Reestablished, start).ConfigureAwait(false);
                await AddLaneRowAsync(testDb, "vsl_expired", start.AddMinutes(-10), 1, 0, 1, LaneBlockReasonEnum.None, "unknown", 7200).ConfigureAwait(false);
                AuthContext auth = AuthContext.Authenticated("default", "default", true, true, "UnitTest");
                ProductionSummaryQuery window = new ProductionSummaryQuery { FromUtc = start, ToUtc = start.AddHours(2) };

                ProductionSummaryResult before = await new VerifiedProductionSummaryService(testDb.Driver).SummarizeAsync(auth, window).ConfigureAwait(false);
                AssertEqual(1, before.Groups.Single().FirstPassAcceptance.Eligible, "the facts are counted while they exist");
                AssertEqual(0, before.Groups.Single().FirstPassAcceptance.Accepted);
                AssertEqual(1, before.Groups.Single().RepeatedResearch.RepeatedClaims!.Value);
                AssertEqual(110.0, before.LaneTime.ObservedLaneMinutes, "the lane row is trusted until ten minutes before the window ends");

                SyslogLogging.LoggingModule logging = new SyslogLogging.LoggingModule();
                logging.Settings.EnableConsole = false;
                await new DataExpiryService(logging, testDb.Driver, 0, 365).PurgeExpiredDataAsync().ConfigureAwait(false);

                ProductionSummaryResult after = await new VerifiedProductionSummaryService(testDb.Driver).SummarizeAsync(auth, window).ConfigureAwait(false);
                ProductionSummaryGroup group = after.Groups.Single();
                AssertEqual(1, group.VerifiedLandedSlices.Count, "operational evidence outlives the facts");
                AssertEqual(0, group.FirstPassAcceptance.Eligible, "a slice whose facts expired is not eligible");
                AssertEqual(0, group.FirstPassAcceptance.Accepted, "an expired review denial never turns into an acceptance");
                AssertTrue(group.FirstPassAcceptance.Rate == null, "no rate is reported from expired facts");
                AssertEqual(1, group.FirstPassAcceptance.UnknownByReason["attempt_facts_not_recorded"]);
                AssertEqual(1, group.RepeatedResearch.UnknownByReason["no_preparation_claims_recorded"]);
                AssertTrue(group.RepeatedResearch.RepeatedClaims == null || group.RepeatedResearch.RepeatedClaims == 0, "expired repeats are not reported");
                AssertEqual(0.0, after.LaneTime.ObservedLaneMinutes, "expired lane rows are not observed time");
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
            string commit = CommitFor(label);
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

        private static async Task CreateRegressionIncidentAsync(
            IncidentService incidents, AuthContext auth, DateTime start, string title,
            RegressionPurposeEnum purpose, RegressionCauseEnum cause, string? objectiveId, string? commit, string? checkRunId)
        {
            await incidents.CreateAsync(auth, new IncidentUpsertRequest
            {
                Title = title,
                DetectedUtc = start.AddHours(3),
                CheckRunId = checkRunId,
                RegressionPurpose = purpose,
                RegressionCause = cause,
                RegressionObjectiveId = objectiveId,
                RegressionLandedCommit = commit
            }).ConfigureAwait(false);
        }

        private static async Task AddObservationAsync(TestDatabase testDb, Objective objective, string claimId, PreparationClaimObservationEnum observation, DateTime start)
        {
            await testDb.Driver.PreparationClaimObservations.CreateAsync(new PreparationClaimObservation
            {
                TenantId = objective.TenantId,
                UserId = objective.UserId,
                ObjectiveId = objective.Id,
                ClaimId = claimId,
                EvidenceFingerprint = new string('a', 64),
                Observation = observation,
                CreatedUtc = start.AddMinutes(10)
            }).ConfigureAwait(false);
        }

        private static async Task AddLaneRowAsync(TestDatabase testDb, string laneKey, DateTime createdUtc, int eligible, int occupied, int capacity, LaneBlockReasonEnum block, string families, int validForSeconds)
        {
            await testDb.Driver.LaneStateTransitions.CreateAsync(new LaneStateTransition
            {
                LaneKey = laneKey,
                EligibleCount = eligible,
                Occupied = occupied,
                Capacity = capacity,
                BlockReason = block,
                EligibleSourceFamilies = families,
                ValidForSeconds = validForSeconds,
                CreatedUtc = createdUtc
            }).ConfigureAwait(false);
        }

        private static string CommitFor(string label)
        {
            using (System.Security.Cryptography.SHA1 sha = System.Security.Cryptography.SHA1.Create())
                return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(label))).Substring(0, 12).ToLowerInvariant();
        }

        private static async Task<Objective> ObjectiveTitledAsync(TestDatabase testDb, string title)
        {
            List<Objective> objectives = await testDb.Driver.Objectives.EnumerateAsync().ConfigureAwait(false);
            return objectives.Single(item => item.Title == title);
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
