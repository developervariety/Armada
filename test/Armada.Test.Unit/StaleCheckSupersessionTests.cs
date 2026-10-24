namespace Armada.Test.Unit
{
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;

    /// <summary>
    /// Verifies that a green Check which measured an earlier commit is replaced, on a live voyage,
    /// by a fresh Pending record for the work now under review, and that the stale record is kept
    /// as Canceled history naming its successor.
    /// </summary>
    public sealed class StaleCheckSupersessionTests : TestSuite
    {
        private const string CommitA = "aaaaaaaa1111111111111111111111111111aaaa";
        private const string CommitB = "bbbbbbbb2222222222222222222222222222bbbb";

        /// <summary>Suite name.</summary>
        public override string Name => "StaleCheckSupersession";

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task<Voyage> SeedVoyageAsync(TestDatabase testDb, VoyageStatusEnum status)
        {
            Voyage voyage = new Voyage("stale-check-voyage");
            voyage.Status = status;
            return await testDb.Driver.Voyages.CreateAsync(voyage).ConfigureAwait(false);
        }

        private static async Task<Mission> SeedStageAsync(
            TestDatabase testDb, Voyage voyage, string persona, MissionStatusEnum status, string commit, DateTime lastUpdate)
        {
            Mission mission = new Mission("[" + persona + "] stage", "description");
            mission.VoyageId = voyage.Id;
            mission.Persona = persona;
            mission.Status = status;
            mission.BranchName = "armada/example/" + persona.ToLowerInvariant();
            mission.CommitHash = commit;
            mission.LastUpdateUtc = lastUpdate;
            mission = await testDb.Driver.Missions.CreateAsync(mission).ConfigureAwait(false);
            // The driver may stamp LastUpdateUtc at creation; pin the intended ordering explicitly.
            mission.LastUpdateUtc = lastUpdate;
            return await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
        }

        private static async Task<CheckRun> SeedCheckAsync(
            TestDatabase testDb, Voyage voyage, CheckRunTypeEnum type, CheckRunStatusEnum status, string? commit)
        {
            CheckRun run = new CheckRun
            {
                VoyageId = voyage.Id,
                VesselId = "vsl_example",
                Type = type,
                Source = CheckRunSourceEnum.Armada,
                Status = status,
                Label = type.ToString() + " (armed at dispatch)",
                Command = status == CheckRunStatusEnum.Pending && commit == null ? "echo" : "dotnet test",
                BranchName = commit == null ? null : "armada/example/worker",
                CommitHash = commit,
                ExitCode = status == CheckRunStatusEnum.Passed ? 0 : null,
                Output = status == CheckRunStatusEnum.Passed ? "Passed!" : null
            };
            return await testDb.Driver.CheckRuns.CreateAsync(run).ConfigureAwait(false);
        }

        private static async Task<List<CheckRun>> ReadChecksAsync(TestDatabase testDb, Voyage voyage)
        {
            EnumerationResult<CheckRun> page = await testDb.Driver.CheckRuns
                .EnumerateAsync(new CheckRunQuery { VoyageId = voyage.Id, PageSize = 100 }).ConfigureAwait(false);
            return page.Objects.OrderBy(run => run.CreatedUtc).ToList();
        }

        private static Mission LaneStage(string id, string? dependsOn, int? stageOrder, int minute, string? commit = null, string? branch = null)
        {
            Mission mission = new Mission("[Stage] " + id, "description");
            mission.Id = id;
            mission.Persona = "Worker";
            mission.Status = MissionStatusEnum.WorkProduced;
            mission.DependsOnMissionId = dependsOn;
            mission.StageOrder = stageOrder;
            mission.CommitHash = commit;
            mission.BranchName = branch;
            mission.CreatedUtc = new DateTime(2020, 1, 1, 0, minute, 0, DateTimeKind.Utc);
            return mission;
        }

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Cancelling a voyage cancels its Pending armed Checks with a named reason", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                Voyage voyage = await SeedVoyageAsync(testDb, VoyageStatusEnum.InProgress).ConfigureAwait(false);
                CheckRun pending = await SeedCheckAsync(testDb, voyage, CheckRunTypeEnum.Build, CheckRunStatusEnum.Pending, null).ConfigureAwait(false);
                CheckRun passed = await SeedCheckAsync(testDb, voyage, CheckRunTypeEnum.UnitTest, CheckRunStatusEnum.Passed, CommitA).ConfigureAwait(false);

                await VoyageCancellation.CancelVoyageAsync(testDb.Driver, voyage, "operator cancel").ConfigureAwait(false);

                CheckRun? pendingAfter = await testDb.Driver.CheckRuns.ReadAsync(pending.Id).ConfigureAwait(false);
                CheckRun? passedAfter = await testDb.Driver.CheckRuns.ReadAsync(passed.Id).ConfigureAwait(false);
                AssertEqual(CheckRunStatusEnum.Canceled, pendingAfter!.Status, "a Pending armed Check cannot outlive its cancelled voyage");
                AssertContains(VoyageCheckDiscard.VoyageCancelledReason, pendingAfter.Summary ?? String.Empty, "the cancel names why the Check never ran");
                AssertEqual(CheckRunStatusEnum.Passed, passedAfter!.Status, "a finished Check is history and stays as it was");
            }).ConfigureAwait(false);

            DateTime earlier = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime later = earlier.AddHours(1);

            await RunTest("SelectWorkUnderReview_PicksTheLatestCommittingStage", () =>
            {
                Mission worker = new Mission("[Worker] w", "d") { Status = MissionStatusEnum.WorkProduced, BranchName = "b", CommitHash = CommitA, LastUpdateUtc = earlier };
                Mission tester = new Mission("[TestEngineer] t", "d") { Status = MissionStatusEnum.Complete, BranchName = "b", CommitHash = CommitB, LastUpdateUtc = later };
                Mission judgePending = new Mission("[Judge] j", "d") { Persona = "Judge", Status = MissionStatusEnum.Pending, BranchName = null, CommitHash = null, LastUpdateUtc = later.AddHours(1) };
                Mission failed = new Mission("[Worker] f", "d") { Status = MissionStatusEnum.Failed, BranchName = "b", CommitHash = "cccccccc3333", LastUpdateUtc = later.AddHours(2) };

                Mission? picked = StaleCheckSupersessionService.SelectWorkUnderReview(new[] { worker, tester, judgePending, failed });
                AssertEqual(CommitB, picked?.CommitHash, "the most recently updated stage with a commit and a live status is the work under review");
                AssertEqual(null, StaleCheckSupersessionService.SelectWorkUnderReview(new[] { judgePending }), "no committing stage means no work under review");
                AssertEqual(null, StaleCheckSupersessionService.SelectWorkUnderReview(null), "a null list is handled");

                // Landing completes predecessors backwards. Those bookkeeping writes must not
                // move the subject back to an earlier commit, even across a report-only stage.
                Mission bridge = new Mission("[Reviewer] bridge", "d")
                {
                    Status = MissionStatusEnum.Complete, DependsOnMissionId = worker.Id,
                    LastUpdateUtc = later.AddHours(3)
                };
                tester.DependsOnMissionId = bridge.Id;
                judgePending.DependsOnMissionId = tester.Id;
                worker.Status = MissionStatusEnum.Complete;
                worker.LastUpdateUtc = later.AddHours(4);
                worker.CompletedUtc = worker.LastUpdateUtc;
                foreach (Mission[] chain in new[]
                {
                    new[] { worker, bridge, tester, judgePending, failed },
                    new[] { failed, judgePending, tester, bridge, worker }
                })
                {
                    AssertEqual(tester.Id, StaleCheckSupersessionService.SelectWorkUnderReview(chain)?.Id,
                        "a dependency descendant outranks an ancestor restamped by landing reconciliation");
                    AssertEqual(tester.Id, StaleCheckSupersessionService.SelectFinalWorkUnderReview(chain)?.Id,
                        "final-work admission uses that same descendant across an ineligible intermediate");
                }
                // A malformed cycle gives neither member strict precedence. Keep the
                // existing timestamp choice rather than removing every measurable commit.
                worker.DependsOnMissionId = tester.Id;
                tester.DependsOnMissionId = worker.Id;
                AssertEqual(worker.Id, StaleCheckSupersessionService.SelectWorkUnderReview(new[] { tester, worker })?.Id,
                    "cyclic dependencies retain timestamp order without losing measurable work");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("NeedsReplacement_OnlyWhenNoSiblingCoversTheWork", () =>
            {
                CheckRun stale = new CheckRun { Id = "chk_stale", Type = CheckRunTypeEnum.UnitTest, Status = CheckRunStatusEnum.Passed, CommitHash = CommitA };
                CheckRun queuedSibling = new CheckRun { Id = "chk_queued", Type = CheckRunTypeEnum.UnitTest, Status = CheckRunStatusEnum.Pending };
                CheckRun runningSibling = new CheckRun { Id = "chk_running", Type = CheckRunTypeEnum.UnitTest, Status = CheckRunStatusEnum.Running };
                CheckRun greenAtTip = new CheckRun { Id = "chk_tip", Type = CheckRunTypeEnum.UnitTest, Status = CheckRunStatusEnum.Passed, CommitHash = CommitB };
                CheckRun otherType = new CheckRun { Id = "chk_build", Type = CheckRunTypeEnum.Build, Status = CheckRunStatusEnum.Pending };
                CheckRun canceledSibling = new CheckRun { Id = "chk_canceled", Type = CheckRunTypeEnum.UnitTest, Status = CheckRunStatusEnum.Canceled };

                AssertTrue(StaleCheckSupersessionService.NeedsReplacement(stale, new[] { stale }, CommitB), "the stale record alone needs a replacement");
                AssertTrue(StaleCheckSupersessionService.NeedsReplacement(stale, null, CommitB), "no siblings means a replacement is needed");
                AssertFalse(StaleCheckSupersessionService.NeedsReplacement(stale, new[] { stale, queuedSibling }, CommitB), "a queued sibling of the same type will be stamped at the current work");
                AssertFalse(StaleCheckSupersessionService.NeedsReplacement(stale, new[] { stale, runningSibling }, CommitB), "a running sibling is already measuring");
                AssertFalse(StaleCheckSupersessionService.NeedsReplacement(stale, new[] { stale, greenAtTip }, CommitB), "a green at the current commit already covers it");
                AssertTrue(StaleCheckSupersessionService.NeedsReplacement(stale, new[] { stale, otherType }, CommitB), "a sibling of another type does not cover this type");
                AssertTrue(StaleCheckSupersessionService.NeedsReplacement(stale, new[] { stale, canceledSibling }, CommitB), "a Canceled sibling covers nothing");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // The failure this reproduces: a Check passed at the first committing stage, later
            // stages moved the branch, and the green stayed green for a tip it never measured.
            await RunTest("StaleGreenOnLiveVoyage_IsCanceledAndReArmed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = await SeedVoyageAsync(testDb, VoyageStatusEnum.InProgress).ConfigureAwait(false);
                    await SeedStageAsync(testDb, voyage, "Worker", MissionStatusEnum.WorkProduced, CommitA, earlier).ConfigureAwait(false);
                    Mission tester = await SeedStageAsync(testDb, voyage, "TestEngineer", MissionStatusEnum.Complete, CommitB, later).ConfigureAwait(false);
                    CheckRun green = await SeedCheckAsync(testDb, voyage, CheckRunTypeEnum.UnitTest, CheckRunStatusEnum.Passed, CommitA).ConfigureAwait(false);

                    StaleCheckSupersessionService svc = new StaleCheckSupersessionService(testDb.Driver, CreateLogging());
                    int first = await svc.SupersedeAsync().ConfigureAwait(false);
                    AssertEqual(1, first, "the one stale green is superseded");

                    List<CheckRun> after = await ReadChecksAsync(testDb, voyage).ConfigureAwait(false);
                    AssertEqual(2, after.Count, "a replacement record was created beside the stale one");

                    CheckRun old = after.First(run => run.Id == green.Id);
                    CheckRun fresh = after.First(run => run.Id != green.Id);
                    AssertEqual(CheckRunStatusEnum.Canceled, old.Status, "the stale green is Canceled, so no gate reads it");
                    AssertEqual(CommitA, old.CommitHash, "the stale record keeps the commit it measured as history");
                    AssertContains(fresh.Id, old.Summary ?? String.Empty, "the stale record names its successor");
                    AssertContains(tester.Id, old.Summary ?? String.Empty, "the stale record names the mission whose work moved on");

                    AssertEqual(CheckRunStatusEnum.Pending, fresh.Status, "the replacement is Pending for the executor");
                    AssertEqual(CheckRunTypeEnum.UnitTest, fresh.Type, "the replacement keeps the type");
                    AssertEqual(voyage.Id, fresh.VoyageId, "the replacement is attached to the same voyage");
                    AssertEqual(null, fresh.CommitHash, "the replacement is unstamped; the executor stamps it at the current work when it runs");
                    AssertTrue(CheckRunGateRules.IsUnexecutedIntentMarker(fresh), "the replacement reads as an armed record until it runs");

                    int second = await svc.SupersedeAsync().ConfigureAwait(false);
                    AssertEqual(0, second, "a second sweep finds nothing: the old record is Canceled and the new one is not a green");
                    List<CheckRun> again = await ReadChecksAsync(testDb, voyage).ConfigureAwait(false);
                    AssertEqual(2, again.Count, "no further records are created");

                    EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events
                        .EnumerateAsync(new EnumerationQuery { PageSize = 100 }).ConfigureAwait(false);
                    AssertTrue(events.Objects.Any(evt => evt.EventType == "check.superseded" && evt.EntityId == green.Id),
                        "the supersession is recorded as an event naming the stale record");
                }
            }).ConfigureAwait(false);

            // A dispatched voyage runs as Open; only a rescue is created InProgress. The first live
            // observation of this service showed it skipping an Open voyage whose analyst had
            // committed on top of the Worker's green, so this pins both live statuses.
            await RunTest("StaleGreenOnOpenVoyage_IsSupersededLikeInProgress", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = await SeedVoyageAsync(testDb, VoyageStatusEnum.Open).ConfigureAwait(false);
                    await SeedStageAsync(testDb, voyage, "Worker", MissionStatusEnum.WorkProduced, CommitA, earlier).ConfigureAwait(false);
                    await SeedStageAsync(testDb, voyage, "PortingReferenceAnalyst", MissionStatusEnum.WorkProduced, CommitB, later).ConfigureAwait(false);
                    CheckRun green = await SeedCheckAsync(testDb, voyage, CheckRunTypeEnum.UnitTest, CheckRunStatusEnum.Passed, CommitA).ConfigureAwait(false);

                    StaleCheckSupersessionService svc = new StaleCheckSupersessionService(testDb.Driver, CreateLogging());
                    AssertTrue(StaleCheckSupersessionService.IsLiveVoyageStatus(VoyageStatusEnum.Open), "Open is a live status");
                    AssertTrue(StaleCheckSupersessionService.IsLiveVoyageStatus(VoyageStatusEnum.InProgress), "InProgress is a live status");
                    AssertFalse(StaleCheckSupersessionService.IsLiveVoyageStatus(VoyageStatusEnum.Complete), "Complete is not");
                    AssertFalse(StaleCheckSupersessionService.IsLiveVoyageStatus(VoyageStatusEnum.Failed), "Failed is not");
                    AssertFalse(StaleCheckSupersessionService.IsLiveVoyageStatus(VoyageStatusEnum.Cancelled), "Cancelled is not");

                    AssertEqual(1, await svc.SupersedeAsync().ConfigureAwait(false), "the stale green on an Open voyage is superseded by the fleet sweep");
                    List<CheckRun> after = await ReadChecksAsync(testDb, voyage).ConfigureAwait(false);
                    AssertEqual(2, after.Count, "a replacement was armed");
                    AssertEqual(CheckRunStatusEnum.Canceled, after.First(run => run.Id == green.Id).Status, "the stale green is Canceled");
                    AssertEqual(CheckRunStatusEnum.Pending, after.First(run => run.Id != green.Id).Status, "the replacement is Pending");
                }
            }).ConfigureAwait(false);

            // A red for an older commit is superseded like a stale green: the later commit may be
            // its fix, and only a record at the tip can say so.
            await RunTest("StaleRedOnLiveVoyage_IsCanceledAndReArmed", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = await SeedVoyageAsync(testDb, VoyageStatusEnum.Open).ConfigureAwait(false);
                    await SeedStageAsync(testDb, voyage, "Worker", MissionStatusEnum.WorkProduced, CommitA, earlier).ConfigureAwait(false);
                    await SeedStageAsync(testDb, voyage, "TestEngineer", MissionStatusEnum.WorkProduced, CommitB, later).ConfigureAwait(false);
                    CheckRun red = await SeedCheckAsync(testDb, voyage, CheckRunTypeEnum.UnitTest, CheckRunStatusEnum.Failed, CommitA).ConfigureAwait(false);

                    StaleCheckSupersessionService svc = new StaleCheckSupersessionService(testDb.Driver, CreateLogging());
                    AssertEqual(1, await svc.SupersedeAsync().ConfigureAwait(false), "the stale red is superseded");
                    List<CheckRun> after = await ReadChecksAsync(testDb, voyage).ConfigureAwait(false);
                    AssertEqual(2, after.Count, "a replacement was armed");
                    CheckRun old = after.First(run => run.Id == red.Id);
                    AssertEqual(CheckRunStatusEnum.Canceled, old.Status, "the stale red is Canceled");
                    AssertContains("failed at", old.Summary ?? String.Empty, "the summary keeps the fact that it failed, not that it passed");
                    AssertEqual(CheckRunStatusEnum.Pending, after.First(run => run.Id != red.Id).Status, "the replacement is Pending");
                }
            }).ConfigureAwait(false);

            await RunTest("GreenAtTheReviewedTip_IsLeftAlone", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = await SeedVoyageAsync(testDb, VoyageStatusEnum.InProgress).ConfigureAwait(false);
                    Mission ancestor = await SeedStageAsync(testDb, voyage, "Worker", MissionStatusEnum.Complete, CommitA, earlier).ConfigureAwait(false);
                    Mission tip = await SeedStageAsync(testDb, voyage, "TestEngineer", MissionStatusEnum.Complete, CommitB, later).ConfigureAwait(false);
                    tip.DependsOnMissionId = ancestor.Id;
                    tip.BranchName = ancestor.BranchName;
                    await testDb.Driver.Missions.UpdateAsync(tip).ConfigureAwait(false);
                    ancestor.LastUpdateUtc = later.AddHours(1);
                    ancestor.CompletedUtc = ancestor.LastUpdateUtc;
                    await testDb.Driver.Missions.UpdateAsync(ancestor).ConfigureAwait(false);
                    await SeedCheckAsync(testDb, voyage, CheckRunTypeEnum.Build, CheckRunStatusEnum.Passed, CommitB).ConfigureAwait(false);

                    StaleCheckSupersessionService svc = new StaleCheckSupersessionService(testDb.Driver, CreateLogging());
                    AssertEqual(0, await svc.SupersedeAsync().ConfigureAwait(false), "a green for the current work is not stale");
                    List<CheckRun> after = await ReadChecksAsync(testDb, voyage).ConfigureAwait(false);
                    AssertEqual(1, after.Count, "nothing was created");
                    AssertEqual(CheckRunStatusEnum.Passed, after[0].Status, "nothing was canceled");
                    List<Mission> missions = await testDb.Driver.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
                    VoyageCompletionVerdict verdict = await VoyageCompletionRule.EvaluateAsync(testDb.Driver, voyage, missions).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Complete, verdict.NewStatus,
                        "a final-tip green still completes the voyage after ancestor bookkeeping writes");
                }
            }).ConfigureAwait(false);

            await RunTest("QueuedSiblingOfSameType_CancelsWithoutReArming", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = await SeedVoyageAsync(testDb, VoyageStatusEnum.InProgress).ConfigureAwait(false);
                    await SeedStageAsync(testDb, voyage, "Worker", MissionStatusEnum.WorkProduced, CommitA, earlier).ConfigureAwait(false);
                    await SeedStageAsync(testDb, voyage, "TestEngineer", MissionStatusEnum.Complete, CommitB, later).ConfigureAwait(false);
                    CheckRun green = await SeedCheckAsync(testDb, voyage, CheckRunTypeEnum.UnitTest, CheckRunStatusEnum.Passed, CommitA).ConfigureAwait(false);
                    CheckRun queued = await SeedCheckAsync(testDb, voyage, CheckRunTypeEnum.UnitTest, CheckRunStatusEnum.Pending, null).ConfigureAwait(false);

                    StaleCheckSupersessionService svc = new StaleCheckSupersessionService(testDb.Driver, CreateLogging());
                    AssertEqual(1, await svc.SupersedeAsync().ConfigureAwait(false), "the stale green is superseded");
                    List<CheckRun> after = await ReadChecksAsync(testDb, voyage).ConfigureAwait(false);
                    AssertEqual(2, after.Count, "no third record: the queued sibling already covers the work");
                    AssertEqual(CheckRunStatusEnum.Canceled, after.First(run => run.Id == green.Id).Status, "the stale green is Canceled");
                    AssertEqual(CheckRunStatusEnum.Pending, after.First(run => run.Id == queued.Id).Status, "the queued sibling is untouched");
                }
            }).ConfigureAwait(false);

            await RunTest("FinishedVoyage_IsNeverTouched", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Voyage voyage = await SeedVoyageAsync(testDb, VoyageStatusEnum.Complete).ConfigureAwait(false);
                    await SeedStageAsync(testDb, voyage, "Worker", MissionStatusEnum.WorkProduced, CommitA, earlier).ConfigureAwait(false);
                    await SeedStageAsync(testDb, voyage, "TestEngineer", MissionStatusEnum.Complete, CommitB, later).ConfigureAwait(false);
                    await SeedCheckAsync(testDb, voyage, CheckRunTypeEnum.UnitTest, CheckRunStatusEnum.Passed, CommitA).ConfigureAwait(false);

                    StaleCheckSupersessionService svc = new StaleCheckSupersessionService(testDb.Driver, CreateLogging());
                    AssertEqual(0, await svc.SupersedeAsync().ConfigureAwait(false), "a completed voyage's history is not rewritten");
                    AssertEqual(0, await svc.SupersedeForVoyageAsync(voyage).ConfigureAwait(false), "even when asked directly");
                    List<CheckRun> after = await ReadChecksAsync(testDb, voyage).ConfigureAwait(false);
                    AssertEqual(CheckRunStatusEnum.Passed, after[0].Status, "the record is untouched");
                }

                foreach (VoyageStatusEnum terminal in new[] { VoyageStatusEnum.Complete, VoyageStatusEnum.Failed, VoyageStatusEnum.Cancelled })
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    Voyage snapshot = await SeedVoyageAsync(testDb, VoyageStatusEnum.Open).ConfigureAwait(false);
                    await SeedStageAsync(testDb, snapshot, "Worker", MissionStatusEnum.WorkProduced, CommitA, earlier).ConfigureAwait(false);
                    await SeedStageAsync(testDb, snapshot, "TestEngineer", MissionStatusEnum.Complete, CommitB, later).ConfigureAwait(false);
                    await SeedCheckAsync(testDb, snapshot, CheckRunTypeEnum.UnitTest, CheckRunStatusEnum.Passed, CommitA).ConfigureAwait(false);
                    Voyage stored = (await testDb.Driver.Voyages.ReadAsync(snapshot.Id).ConfigureAwait(false))!;
                    stored.Status = terminal;
                    await testDb.Driver.Voyages.UpdateAsync(stored).ConfigureAwait(false);

                    StaleCheckSupersessionService svc = new StaleCheckSupersessionService(testDb.Driver, CreateLogging());
                    AssertEqual(VoyageStatusEnum.Open, snapshot.Status, "the caller still holds an old live snapshot");
                    AssertEqual(0, await svc.SupersedeForVoyageAsync(snapshot).ConfigureAwait(false),
                        terminal + ": durable terminal status wins over the stale live caller");
                    List<CheckRun> after = await ReadChecksAsync(testDb, snapshot).ConfigureAwait(false);
                    AssertEqual(1, after.Count, terminal + ": no replacement was created");
                    AssertEqual(CheckRunStatusEnum.Passed, after[0].Status, terminal + ": measured history is untouched");
                }
            }).ConfigureAwait(false);
            // A planner that splits its plan runs one line of work per branch. A green measured on
            // one lane is current for that lane even when another lane committed later, so the sweep
            // must not move records from lane to lane.
            await RunTest("FanOut_EachLaneKeepsItsOwnGreen", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FanOutVoyageFixture fx = await FanOutVoyageFixture.CreateAsync(testDb).ConfigureAwait(false);
                    await fx.AddVoyageCheckAsync(testDb, CheckRunTypeEnum.Build, CheckRunStatusEnum.Passed, FanOutVoyageFixture.LaneABranch, FanOutVoyageFixture.LaneACommit).ConfigureAwait(false);
                    await fx.AddVoyageCheckAsync(testDb, CheckRunTypeEnum.UnitTest, CheckRunStatusEnum.Passed, FanOutVoyageFixture.LaneABranch, FanOutVoyageFixture.LaneACommit).ConfigureAwait(false);
                    await fx.AddVoyageCheckAsync(testDb, CheckRunTypeEnum.Build, CheckRunStatusEnum.Passed, FanOutVoyageFixture.LaneBBranch, FanOutVoyageFixture.LaneBCommit).ConfigureAwait(false);
                    await fx.AddVoyageCheckAsync(testDb, CheckRunTypeEnum.UnitTest, CheckRunStatusEnum.Passed, FanOutVoyageFixture.LaneBBranch, FanOutVoyageFixture.LaneBCommit).ConfigureAwait(false);

                    StaleCheckSupersessionService svc = new StaleCheckSupersessionService(testDb.Driver, CreateLogging());
                    AssertEqual(0, await svc.SupersedeForVoyageAsync(fx.Voyage).ConfigureAwait(false), "no lane's green is stale for its own lane");
                    AssertEqual(0, await svc.SupersedeForVoyageAsync(fx.Voyage).ConfigureAwait(false), "a second sweep changes nothing either");

                    List<CheckRun> after = await ReadChecksAsync(testDb, fx.Voyage).ConfigureAwait(false);
                    AssertEqual(4, after.Count, "no record is re-armed");
                    AssertTrue(after.All(run => run.Status == CheckRunStatusEnum.Passed), "every lane keeps its green");
                }
            }).ConfigureAwait(false);

            await RunTest("FanOut_PlannerCommitRecordIsRetiredOnceEveryLaneIsCovered", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FanOutVoyageFixture fx = await FanOutVoyageFixture.CreateAsync(testDb).ConfigureAwait(false);
                    CheckRun plan = await fx.AddVoyageCheckAsync(testDb, CheckRunTypeEnum.Build, CheckRunStatusEnum.Passed, FanOutVoyageFixture.PlanBranch, FanOutVoyageFixture.PlanCommit).ConfigureAwait(false);
                    CheckRun laneA = await fx.AddVoyageCheckAsync(testDb, CheckRunTypeEnum.Build, CheckRunStatusEnum.Passed, FanOutVoyageFixture.LaneABranch, FanOutVoyageFixture.LaneACommit).ConfigureAwait(false);
                    CheckRun laneB = await fx.AddVoyageCheckAsync(testDb, CheckRunTypeEnum.Build, CheckRunStatusEnum.Passed, FanOutVoyageFixture.LaneBBranch, FanOutVoyageFixture.LaneBCommit).ConfigureAwait(false);

                    StaleCheckSupersessionService svc = new StaleCheckSupersessionService(testDb.Driver, CreateLogging());
                    AssertEqual(1, await svc.SupersedeForVoyageAsync(fx.Voyage).ConfigureAwait(false), "only the planner-commit record is retired");

                    List<CheckRun> after = await ReadChecksAsync(testDb, fx.Voyage).ConfigureAwait(false);
                    AssertEqual(3, after.Count, "no replacement: both lanes already have a verdict of their own");
                    AssertEqual(CheckRunStatusEnum.Canceled, after.Single(run => run.Id == plan.Id).Status, "a record above the split decides no lane");
                    AssertEqual(CheckRunStatusEnum.Passed, after.Single(run => run.Id == laneA.Id).Status, "lane A keeps its green");
                    AssertEqual(CheckRunStatusEnum.Passed, after.Single(run => run.Id == laneB.Id).Status, "lane B keeps its green");
                }
            }).ConfigureAwait(false);

            await RunTest("VoyageLanes_OnlyAStepWithSeveralDependentsSplitsTheVoyage", () =>
            {
                Mission plan = LaneStage("msn_plan", null, null, 0);
                Mission worker = LaneStage("msn_worker", "msn_plan", null, 1);
                Mission judge = LaneStage("msn_judge", "msn_worker", null, 2);
                IReadOnlyList<VoyageLane> chain = VoyageLanes.Resolve(new List<Mission> { judge, plan, worker });
                AssertEqual(1, chain.Count, "a chain is one lane");
                AssertTrue(chain[0].Owns("msn_plan") && chain[0].Owns("msn_judge"), "the one lane owns every stage");
                AssertFalse(VoyageLanes.IsFanOut(chain), "a chain is not a fan-out");

                Mission laneA = LaneStage("msn_a", "msn_plan", null, 1, "a1a1a1a1a1a1", "lane-a");
                Mission laneAJudge = LaneStage("msn_a_judge", "msn_a", null, 3);
                Mission laneB = LaneStage("msn_b", "msn_plan", null, 2, "b1b1b1b1b1b1", "lane-b");
                IReadOnlyList<VoyageLane> fan = VoyageLanes.Resolve(new List<Mission> { plan, laneA, laneAJudge, laneB });
                AssertEqual(2, fan.Count, "two stages that depend on the planner make two lanes");
                VoyageLane fanA = fan.Single(lane => lane.Contains("msn_a"));
                VoyageLane fanB = fan.Single(lane => lane.Contains("msn_b"));
                AssertTrue(fanA.Contains("msn_plan") && fanB.Contains("msn_plan"), "the planner is in both lanes");
                AssertFalse(fanA.Owns("msn_plan") || fanB.Owns("msn_plan"), "and owned by neither");
                AssertTrue(fanA.Owns("msn_a") && fanA.Owns("msn_a_judge") && !fanA.Contains("msn_b"), "lane A holds its Worker and its Judge only");
                AssertTrue(fanB.Owns("msn_b") && !fanB.Contains("msn_a"), "lane B holds its Worker only");
                AssertTrue(ReferenceEquals(fanA, VoyageLanes.LaneOfMission(fan, laneAJudge)), "a Judge's lane is its dependency chain");
                AssertEqual(null, VoyageLanes.LaneOfMission(fan, plan), "the planner belongs to no single lane");

                Mission groupOne = LaneStage("msn_g1", "msn_plan", 1, 1);
                Mission groupTwo = LaneStage("msn_g2", "msn_plan", 1, 2);
                Mission next = LaneStage("msn_next", "msn_g2", 2, 3);
                AssertEqual(1, VoyageLanes.Resolve(new List<Mission> { plan, groupOne, groupTwo, next }).Count,
                    "members of one parallel stage group are one step of one lane");
                groupOne.BranchName = groupTwo.BranchName = next.BranchName = "group-branch";
                groupOne.CommitHash = groupTwo.CommitHash = CommitA;
                next.CommitHash = CommitB;
                groupOne.LastUpdateUtc = later.AddHours(4);
                groupTwo.LastUpdateUtc = later.AddHours(3);
                next.LastUpdateUtc = earlier;
                AssertEqual(next.Id, StaleCheckSupersessionService.SelectWorkUnderReview(
                    new[] { groupOne, next, plan, groupTwo })?.Id,
                    "a descendant outranks every predecessor in the parallel step, including the unnamed member");
                AssertEqual(groupOne.Id, StaleCheckSupersessionService.SelectWorkUnderReview(
                    new[] { groupTwo, plan, groupOne })?.Id,
                    "parallel peers do not displace one another by dependency order");

                Mission loneOne = LaneStage("msn_l1", null, null, 0);
                Mission loneTwo = LaneStage("msn_l2", null, null, 1);
                AssertEqual(1, VoyageLanes.Resolve(new List<Mission> { loneOne, loneTwo }).Count,
                    "stages with no dependency at all keep the single-lane behaviour");

                CheckRun byCommit = new CheckRun { CommitHash = "b1b1b1b1b1b1" };
                CheckRun byBranch = new CheckRun { BranchName = "LANE-A" };
                CheckRun unstamped = new CheckRun();
                CheckRun above = new CheckRun { MissionId = "msn_plan" };
                AssertTrue(ReferenceEquals(fanB, VoyageLanes.SingleLaneOfRecord(fan, byCommit)), "a record measured at a lane's commit belongs to that lane");
                AssertTrue(ReferenceEquals(fanA, VoyageLanes.SingleLaneOfRecord(fan, byBranch)), "a record on a lane's branch belongs to that lane");
                AssertEqual(2, VoyageLanes.LanesOfRecord(fan, unstamped).Count, "an unstamped record belongs to every lane");
                AssertEqual(2, VoyageLanes.LanesOfRecord(fan, above).Count, "a record of the planner belongs to every lane");
                AssertEqual(null, VoyageLanes.SingleLaneOfRecord(fan, unstamped), "so it decides no lane");
                AssertTrue(ReferenceEquals(chain[0], VoyageLanes.SingleLaneOfRecord(chain, unstamped)), "on a single lane every record decides the one lane");
            }).ConfigureAwait(false);
            // The Judge gate, the executor's eligibility and stamping, and the supersession all ask
            // the same question about a record: which lane does it measure? Compared record by
            // record, they must give the same answer, and on a voyage that has not split they must
            // give the answer they gave before lanes existed.
            await RunTest("FanOut_EveryEntryPointAgreesOnARecordsLane", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    FanOutVoyageFixture fx = await FanOutVoyageFixture.CreateAsync(testDb, laneATester: true).ConfigureAwait(false);
                    fx.LaneAWorker.LastUpdateUtc = later.AddHours(1);
                    await testDb.Driver.Missions.UpdateAsync(fx.LaneAWorker).ConfigureAwait(false);
                    fx.Architect.LastUpdateUtc = later.AddHours(2);
                    await testDb.Driver.Missions.UpdateAsync(fx.Architect).ConfigureAwait(false);
                    List<CheckRun> records = new List<CheckRun>
                    {
                        new CheckRun { Id = "chk_lane_a", BranchName = FanOutVoyageFixture.LaneABranch, CommitHash = FanOutVoyageFixture.LaneACommit },
                        new CheckRun { Id = "chk_lane_a_old", BranchName = FanOutVoyageFixture.LaneABranch, CommitHash = FanOutVoyageFixture.LaneAWorkerCommit },
                        new CheckRun { Id = "chk_lane_a_branch", BranchName = FanOutVoyageFixture.LaneABranch },
                        new CheckRun { Id = "chk_lane_b", BranchName = FanOutVoyageFixture.LaneBBranch, CommitHash = FanOutVoyageFixture.LaneBCommit },
                        new CheckRun { Id = "chk_lane_a_bound", MissionId = fx.LaneATester!.Id, BranchName = FanOutVoyageFixture.PlanBranch, CommitHash = FanOutVoyageFixture.PlanCommit },
                        new CheckRun { Id = "chk_plan", BranchName = FanOutVoyageFixture.PlanBranch, CommitHash = FanOutVoyageFixture.PlanCommit },
                        new CheckRun { Id = "chk_unstamped" }
                    };
                    foreach (CheckRun run in records) run.VoyageId = fx.Voyage.Id;

                    List<Mission> missions = await testDb.Driver.Missions.EnumerateByVoyageAsync(fx.Voyage.Id).ConfigureAwait(false);
                    int decided = 0;
                    foreach (CheckRun run in records)
                    {
                        bool judgeA = VoyageLanes.ChecksForLaneOf(fx.LaneAJudge, missions, new List<CheckRun> { run }).Count == 1;
                        bool judgeB = VoyageLanes.ChecksForLaneOf(fx.LaneBJudge, missions, new List<CheckRun> { run }).Count == 1;
                        Mission? work = StaleCheckSupersessionService.SelectWorkUnderReviewFor(run, missions);
                        Mission? final = StaleCheckSupersessionService.SelectFinalWorkUnderReviewFor(run, missions);

                        AssertFalse(judgeA && judgeB, run.Id + " decides at most one Judge");
                        AssertEqual(judgeA, work?.Id == fx.LaneATester!.Id, run.Id + ": lane A's Judge reads it exactly when it measures lane A's work");
                        AssertEqual(judgeB, work?.Id == fx.LaneBWorker.Id, run.Id + ": lane B's Judge reads it exactly when it measures lane B's work");
                        AssertEqual(work?.Id, final?.Id, run.Id + ": with every lane done, eligibility and stamping name the same work");
                        if (judgeA || judgeB) decided++;
                    }
                    AssertEqual(5, decided, "the four unique-source records and the mission-bound shared-source record decide a lane; the planner and unstamped records decide none");

                    List<Mission> chain = missions.Where(mission => mission.Id != fx.LaneBWorker.Id && mission.Id != fx.LaneBJudge.Id).ToList();
                    foreach (CheckRun run in records)
                    {
                        AssertEqual(1, VoyageLanes.ChecksForLaneOf(fx.LaneAJudge, chain, new List<CheckRun> { run }).Count,
                            run.Id + ": on a voyage that has not split the Judge reads every record");
                        AssertEqual(StaleCheckSupersessionService.SelectWorkUnderReview(chain)?.Id,
                            StaleCheckSupersessionService.SelectWorkUnderReviewFor(run, chain)?.Id,
                            run.Id + ": on a voyage that has not split the record measures the voyage's work");
                        AssertEqual(StaleCheckSupersessionService.SelectFinalWorkUnderReview(chain)?.Id,
                            StaleCheckSupersessionService.SelectFinalWorkUnderReviewFor(run, chain)?.Id,
                            run.Id + ": on a voyage that has not split eligibility is unchanged");
                    }
                }
            }).ConfigureAwait(false);
        }
    }
}
