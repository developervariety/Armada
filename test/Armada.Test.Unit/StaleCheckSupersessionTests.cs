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

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            DateTime earlier = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime later = earlier.AddHours(1);

            await RunTest("SelectWorkUnderReview_PicksTheLatestCommittingStage", () =>
            {
                Mission worker = new Mission("[Worker] w", "d") { Status = MissionStatusEnum.WorkProduced, BranchName = "b", CommitHash = CommitA, LastUpdateUtc = earlier };
                Mission tester = new Mission("[TestEngineer] t", "d") { Status = MissionStatusEnum.Complete, BranchName = "b", CommitHash = CommitB, LastUpdateUtc = later };
                Mission judgePending = new Mission("[Judge] j", "d") { Status = MissionStatusEnum.Pending, BranchName = null, CommitHash = null, LastUpdateUtc = later.AddHours(1) };
                Mission failed = new Mission("[Worker] f", "d") { Status = MissionStatusEnum.Failed, BranchName = "b", CommitHash = "cccccccc3333", LastUpdateUtc = later.AddHours(2) };

                Mission? picked = StaleCheckSupersessionService.SelectWorkUnderReview(new[] { worker, tester, judgePending, failed });
                AssertEqual(CommitB, picked?.CommitHash, "the most recently updated stage with a commit and a live status is the work under review");
                AssertEqual(null, StaleCheckSupersessionService.SelectWorkUnderReview(new[] { judgePending }), "no committing stage means no work under review");
                AssertEqual(null, StaleCheckSupersessionService.SelectWorkUnderReview(null), "a null list is handled");
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
                    await SeedStageAsync(testDb, voyage, "Worker", MissionStatusEnum.WorkProduced, CommitB, later).ConfigureAwait(false);
                    await SeedCheckAsync(testDb, voyage, CheckRunTypeEnum.Build, CheckRunStatusEnum.Passed, CommitB).ConfigureAwait(false);

                    StaleCheckSupersessionService svc = new StaleCheckSupersessionService(testDb.Driver, CreateLogging());
                    AssertEqual(0, await svc.SupersedeAsync().ConfigureAwait(false), "a green for the current work is not stale");
                    List<CheckRun> after = await ReadChecksAsync(testDb, voyage).ConfigureAwait(false);
                    AssertEqual(1, after.Count, "nothing was created");
                    AssertEqual(CheckRunStatusEnum.Passed, after[0].Status, "nothing was canceled");
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
            }).ConfigureAwait(false);
        }
    }
}
