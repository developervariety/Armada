namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using TestResourcePressure = global::Test.Shared.Infrastructure.TestResourcePressure;
    using SyslogLogging;

    /// <summary>
    /// Tests for stage-level manual review gates in pipeline dispatch.
    /// </summary>
    public class ReviewGateTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "Review Gate Workflows";

        private readonly List<string> _FixtureDirectories = new List<string>();

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private ArmadaSettings CreateSettings()
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_review_docks_" + Guid.NewGuid().ToString("N"));
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_review_repos_" + Guid.NewGuid().ToString("N"));
            _FixtureDirectories.Add(settings.DocksDirectory);
            _FixtureDirectories.Add(settings.ReposDirectory);
            return settings;
        }

        /// <summary>
        /// Run the review-gate workflow tests.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            try
            {
                await RunReviewCasesAsync().ConfigureAwait(false);
            }
            finally
            {
                foreach (string path in _FixtureDirectories)
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                _FixtureDirectories.Clear();
            }
        }

        private async Task RunReviewCasesAsync()
        {
            await RunTest("Single-stage review retains dock until approval invokes completion callback", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    ReviewScenario scenario = await CreateScenarioAsync(testDb.Driver, includeDownstreamStage: false).ConfigureAwait(false);

                    scenario.Missions.OnMissionComplete = async (mission, dock) =>
                    {
                        mission.Status = MissionStatusEnum.Complete;
                        mission.CompletedUtc = DateTime.UtcNow;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
                    };

                    await scenario.Missions.HandleCompletionAsync(scenario.WorkerCaptain, scenario.WorkerMission.Id).ConfigureAwait(false);

                    Mission? awaitingApproval = await testDb.Driver.Missions.ReadAsync(scenario.WorkerMission.Id).ConfigureAwait(false);
                    Dock? retainedDock = awaitingApproval?.DockId == null
                        ? null
                        : await testDb.Driver.Docks.ReadAsync(awaitingApproval.DockId).ConfigureAwait(false);

                    AssertNotNull(awaitingApproval, "Single-stage mission should still exist");
                    AssertEqual(MissionStatusEnum.Review, awaitingApproval!.Status, "Single-stage worker pipeline should honor the review gate");
                    AssertNotNull(retainedDock, "Terminal review should retain the dock until approval");
                    AssertTrue(retainedDock!.Active, "Retained dock should stay active while terminal review is pending");

                    await scenario.Missions.ApproveReviewAsync(scenario.WorkerMission.Id, "usr_reviewer", "Ship it").ConfigureAwait(false);

                    Mission? completed = await testDb.Driver.Missions.ReadAsync(scenario.WorkerMission.Id).ConfigureAwait(false);
                    Dock? reclaimedDock = retainedDock == null
                        ? null
                        : await testDb.Driver.Docks.ReadAsync(retainedDock.Id).ConfigureAwait(false);

                    AssertNotNull(completed, "Approved single-stage mission should still exist");
                    AssertEqual(MissionStatusEnum.Complete, completed!.Status, "Completion callback should run after review approval");
                    AssertNotNull(reclaimedDock, "Dock row should remain readable after reclaim");
                    AssertFalse(reclaimedDock!.Active, "Dock should be reclaimed after the completion callback");
                }
            });

            await RunTest("A Judge PASS held by review substance stays held and does not land", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    HeldJudgeScenario held = await CompleteHeldJudgeAsync(testDb).ConfigureAwait(false);

                    Mission? judge = await testDb.Driver.Missions.ReadAsync(held.Judge.Id).ConfigureAwait(false);
                    AssertNotNull(judge, "Judge mission should still exist");
                    AssertEqual(0, held.LandedMissionIds.Count, "A held Judge PASS must not reach the landing handler");
                    AssertEqual(MissionStatusEnum.WorkProduced, judge!.Status, "A held Judge PASS stays WorkProduced");
                    AssertTrue(judge.HeldForOperatorReview, "The Judge PASS is held for operator review");
                    AssertContains("review_substance", judge.HeldForOperatorReviewReason ?? String.Empty, "The hold carries the review-substance reason");
                    Dock? dock = judge.DockId == null ? null : await testDb.Driver.Docks.ReadAsync(judge.DockId).ConfigureAwait(false);
                    AssertNotNull(dock, "The held Judge keeps its dock for the later landing");
                    AssertTrue(dock!.Active, "The held Judge dock stays active");
                    Voyage? heldVoyage = await testDb.Driver.Voyages.ReadAsync(held.Scenario.Voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.InProgress, heldVoyage!.Status, "A held review keeps its voyage open until the operator decides");
                    await new VoyageService(CreateLogging(), testDb.Driver).CheckCompletionsAsync().ConfigureAwait(false);
                    heldVoyage = await testDb.Driver.Voyages.ReadAsync(held.Scenario.Voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.InProgress, heldVoyage!.Status, "The health-cycle sweep does not complete a voyage with a held review");

                    // A duplicate completion report for the held Judge does not land it either.
                    Captain judgeCaptain = (await testDb.Driver.Captains.ReadAsync(judge.CaptainId!).ConfigureAwait(false))!;
                    await held.Scenario.Missions.HandleCompletionAsync(judgeCaptain, judge.Id).ConfigureAwait(false);
                    AssertEqual(0, held.LandedMissionIds.Count, "A repeated completion of a held Judge PASS must not land it");
                }
            });

            await RunTest("Clearing a held Judge PASS lands it and records the operator and reason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    HeldJudgeScenario held = await CompleteHeldJudgeAsync(testDb).ConfigureAwait(false);
                    AssertEqual(0, held.LandedMissionIds.Count, "Fixture: the PASS is held before the clear");

                    const string toolName = "armada_review_hold";
                    Func<System.Text.Json.JsonElement?, Task<object>>? handler = null;
                    Armada.Server.Mcp.Tools.McpReviewHoldTools.Register(
                        (name, _, _, registered) => { if (name == toolName) handler = registered; },
                        held.Scenario.Missions);
                    AssertNotNull(handler, toolName + " is registered");

                    AuthContext tenantAdmin = AuthContext.Authenticated(Armada.Core.Constants.DefaultTenantId, "usr_hold_admin", false, true, "Test");
                    AssertFalse(Armada.Server.Mcp.McpToolAccessPolicy.IsAllowed(tenantAdmin, toolName), "a mission-scoped or narrower caller cannot reach the hold tool");
                    string refusedJson;
                    using (Armada.Server.Mcp.McpCallerContext.Begin(tenantAdmin))
                    {
                        refusedJson = System.Text.Json.JsonSerializer.Serialize(await handler!(System.Text.Json.JsonSerializer.SerializeToElement(
                            new { action = "clear", missionId = held.Judge.Id, reason = "looks fine", @operator = "someone" })).ConfigureAwait(false));
                    }
                    AssertContains(Armada.Server.Mcp.Tools.McpReviewHoldTools.GlobalAdministratorRequiredReason, refusedJson, "a non-operator caller is refused");
                    AssertEqual(0, held.LandedMissionIds.Count, "A refused clear lands nothing");

                    string missingReasonJson = System.Text.Json.JsonSerializer.Serialize(await McpTestCaller.Wrap(handler!)(System.Text.Json.JsonSerializer.SerializeToElement(
                        new { action = "clear", missionId = held.Judge.Id, @operator = "operator-a" })).ConfigureAwait(false));
                    AssertContains("missing_reason", missingReasonJson, "a clear without a reason is refused");

                    List<string> completedVoyageIds = new List<string>();
                    held.Scenario.Admiral.OnVoyageComplete = voyage =>
                    {
                        completedVoyageIds.Add(voyage.Id);
                        return Task.CompletedTask;
                    };

                    string clearedJson = System.Text.Json.JsonSerializer.Serialize(await McpTestCaller.Wrap(handler!)(System.Text.Json.JsonSerializer.SerializeToElement(
                        new { action = "clear", missionId = held.Judge.Id, reason = "read the diff; the review is adequate", @operator = "operator-a" })).ConfigureAwait(false));
                    AssertFalse(clearedJson.Contains("\"Error\"", StringComparison.Ordinal), "the operator clear succeeds: " + clearedJson);

                    Voyage? endedVoyage = await testDb.Driver.Voyages.ReadAsync(held.Scenario.Voyage.Id).ConfigureAwait(false);
                    AssertEqual(VoyageStatusEnum.Complete, endedVoyage!.Status, "The landed PASS completes its voyage on the mission path");
                    AssertEqual(1, completedVoyageIds.Count, "The mission path raises the admiral's voyage completion hook once");
                    AssertEqual(held.Scenario.Voyage.Id, completedVoyageIds[0]);

                    Mission? cleared = await testDb.Driver.Missions.ReadAsync(held.Judge.Id).ConfigureAwait(false);
                    AssertEqual(1, held.LandedMissionIds.Count, "A cleared hold lets the PASS reach the landing handler");
                    AssertEqual(held.Judge.Id, held.LandedMissionIds[0]);
                    AssertFalse(cleared!.HeldForOperatorReview, "The hold is cleared");
                    AssertNull(cleared.HeldForOperatorReviewReason, "A cleared hold keeps no reason");
                    AssertEqual(MissionStatusEnum.Complete, cleared.Status, "The cleared PASS completes through the normal landing path");

                    EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                    {
                        EventType = MissionService.OperatorHoldClearedEventType, PageNumber = 1, PageSize = 10
                    }).ConfigureAwait(false);
                    AssertEqual(1, events.Objects.Count, "One mission.hold_cleared event is recorded");
                    AssertEqual(held.Judge.Id, events.Objects[0].EntityId);
                    AssertContains("operator-a", events.Objects[0].Message ?? String.Empty, "The event names the operator");
                    AssertContains("read the diff; the review is adequate", events.Objects[0].Message ?? String.Empty, "The event names the reason");

                    string againJson = System.Text.Json.JsonSerializer.Serialize(await McpTestCaller.Wrap(handler!)(System.Text.Json.JsonSerializer.SerializeToElement(
                        new { action = "fail", missionId = held.Judge.Id, reason = "too late", @operator = "operator-a" })).ConfigureAwait(false));
                    AssertContains("not_held", againJson, "a mission that is no longer held cannot be failed through the hold tool");
                }
            });

            await RunTest("Failing a held Judge PASS fails it without landing and records the operator and reason", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    HeldJudgeScenario held = await CompleteHeldJudgeAsync(testDb).ConfigureAwait(false);

                    Mission failed = await held.Scenario.Missions.FailOperatorReviewHoldAsync(
                        held.Judge.Id, "operator-b", "the review never checked the decoder boundary").ConfigureAwait(false);

                    Mission? reloaded = await testDb.Driver.Missions.ReadAsync(held.Judge.Id).ConfigureAwait(false);
                    AssertEqual(0, held.LandedMissionIds.Count, "A failed hold never reaches the landing handler");
                    AssertEqual(MissionStatusEnum.Failed, failed.Status);
                    AssertEqual(MissionStatusEnum.Failed, reloaded!.Status, "The failed hold persists as Failed");
                    AssertFalse(reloaded.HeldForOperatorReview, "A failed mission is no longer held");
                    AssertContains("operator_review_hold_failed", reloaded.FailureReason ?? String.Empty, "The failure reason names the operator decision");
                    AssertContains("operator-b", reloaded.FailureReason ?? String.Empty, "The failure reason names the operator");

                    EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                    {
                        EventType = MissionService.OperatorHoldFailedEventType, PageNumber = 1, PageSize = 10
                    }).ConfigureAwait(false);
                    AssertEqual(1, events.Objects.Count, "One mission.hold_failed event is recorded");
                    AssertContains("operator-b", events.Objects[0].Message ?? String.Empty, "The event names the operator");
                    AssertContains("the review never checked the decoder boundary", events.Objects[0].Message ?? String.Empty, "The event names the reason");
                }
            });

            // A voyage in the voyage terminal set is never rewritten by completion: an operator decision
            // on a held review under a cancelled voyage settles the mission and leaves the voyage alone.
            await RunTest("Failing a held Judge PASS under a cancelled voyage keeps the voyage cancelled", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    HeldJudgeScenario held = await CompleteHeldJudgeAsync(testDb).ConfigureAwait(false);
                    DateTime cancelledUtc = await CancelVoyageRecordAsync(testDb, held.Scenario.Voyage.Id).ConfigureAwait(false);

                    await held.Scenario.Missions.FailOperatorReviewHoldAsync(
                        held.Judge.Id, "operator-c", "the voyage was abandoned").ConfigureAwait(false);

                    Mission? judge = await testDb.Driver.Missions.ReadAsync(held.Judge.Id).ConfigureAwait(false);
                    Voyage? voyage = await testDb.Driver.Voyages.ReadAsync(held.Scenario.Voyage.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Failed, judge!.Status, "The held mission is failed");
                    AssertEqual(VoyageStatusEnum.Cancelled, voyage!.Status, "The cancelled voyage is not rewritten to Failed");
                    AssertEqual(cancelledUtc, voyage.CompletedUtc, "The cancel time is not rewritten");
                }
            });

            await RunTest("Clearing a held Judge PASS under a cancelled voyage keeps the voyage cancelled", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    HeldJudgeScenario held = await CompleteHeldJudgeAsync(testDb).ConfigureAwait(false);
                    DateTime cancelledUtc = await CancelVoyageRecordAsync(testDb, held.Scenario.Voyage.Id).ConfigureAwait(false);

                    await held.Scenario.Missions.ClearOperatorReviewHoldAsync(
                        held.Judge.Id, "operator-c", "the review is adequate").ConfigureAwait(false);

                    Mission? judge = await testDb.Driver.Missions.ReadAsync(held.Judge.Id).ConfigureAwait(false);
                    Voyage? voyage = await testDb.Driver.Voyages.ReadAsync(held.Scenario.Voyage.Id).ConfigureAwait(false);
                    AssertEqual(1, held.LandedMissionIds.Count, "The cleared PASS still reaches the landing handler");
                    AssertEqual(MissionStatusEnum.Complete, judge!.Status, "The cleared PASS completes");
                    AssertEqual(VoyageStatusEnum.Cancelled, voyage!.Status, "The cancelled voyage is not rewritten to Complete");
                    AssertEqual(cancelledUtc, voyage.CompletedUtc, "The cancel time is not rewritten");
                }
            });

            await RunTest("A Judge PASS waiting on its Checks is held, not re-run, and lands when they pass at the reviewed commit", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    CheckHeldJudgeScenario held = await CompleteJudgeWaitingOnChecksAsync(testDb).ConfigureAwait(false);

                    Mission? waiting = await testDb.Driver.Missions.ReadAsync(held.Judge.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.WorkProduced, waiting!.Status, "The PASS is held as it stands, not reset for a new Judge run: " + waiting.FailureReason);
                    AssertContains("[ARMADA:VERDICT] PASS", waiting.AgentOutput ?? String.Empty, "The finished review is kept");
                    AssertEqual(0, waiting.RecoveryAttempts, "Waiting on Checks spends no recovery budget");
                    AssertTrue(JudgeCheckWaitHold.IsHeld(waiting), "The PASS waits in the check-wait hold: " + waiting.HeldForOperatorReviewReason);
                    AssertContains(_JudgeReviewedCommit, waiting.HeldForOperatorReviewReason ?? String.Empty, "The hold names the reviewed commit");
                    AssertContains(held.UnitTest.Id, waiting.HeldForOperatorReviewReason ?? String.Empty, "The hold names the Check it waits on");
                    AssertEqual(0, held.Landed.LandedMissionIds.Count, "A held PASS does not land");

                    AssertEqual(0, await held.Landed.Scenario.Missions.ReleaseJudgeCheckWaitHoldsAsync().ConfigureAwait(false),
                        "Nothing is decided while the Check at the reviewed commit is still running");

                    await SetCheckAsync(testDb, held.UnitTest.Id, CheckRunStatusEnum.Passed, _JudgeReviewedCommit).ConfigureAwait(false);
                    AssertEqual(1, await held.Landed.Scenario.Missions.ReleaseJudgeCheckWaitHoldsAsync().ConfigureAwait(false),
                        "The held PASS is decided once its Checks pass");

                    Mission? released = await testDb.Driver.Missions.ReadAsync(held.Judge.Id).ConfigureAwait(false);
                    AssertEqual(1, held.Landed.LandedMissionIds.Count, "The released PASS reaches the landing handler without a new Judge run");
                    AssertEqual(held.Judge.Id, held.Landed.LandedMissionIds[0]);
                    AssertEqual(MissionStatusEnum.Complete, released!.Status, "The released PASS completes through the normal landing path");
                    AssertFalse(released.HeldForOperatorReview, "The hold is cleared");
                    AssertEqual(0, released.RecoveryAttempts, "The release spends no recovery budget");

                    EnumerationResult<ArmadaEvent> events = await testDb.Driver.Events.EnumerateAsync(new EnumerationQuery
                    {
                        EventType = MissionService.CheckWaitHoldDecidedEventType, PageNumber = 1, PageSize = 10
                    }).ConfigureAwait(false);
                    AssertEqual(1, events.Objects.Count, "One decision event is recorded");
                    AssertContains("released", events.Objects[0].Message ?? String.Empty, "The event says the PASS was released");
                }
            });

            await RunTest("A held Judge PASS is never released by a green at another commit or by an operator clear", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    CheckHeldJudgeScenario held = await CompleteJudgeWaitingOnChecksAsync(testDb).ConfigureAwait(false);
                    AssertTrue(JudgeCheckWaitHold.IsHeld(await testDb.Driver.Missions.ReadAsync(held.Judge.Id).ConfigureAwait(false)),
                        "Fixture: the PASS waits in the check-wait hold");

                    await SetCheckAsync(testDb, held.UnitTest.Id, CheckRunStatusEnum.Passed, _OtherCommit).ConfigureAwait(false);
                    AssertEqual(0, await held.Landed.Scenario.Missions.ReleaseJudgeCheckWaitHoldsAsync().ConfigureAwait(false),
                        "A green for another commit says nothing about the reviewed commit");

                    InvalidOperationException? refused = null;
                    try
                    {
                        await held.Landed.Scenario.Missions.ClearOperatorReviewHoldAsync(held.Judge.Id, "operator-d", "looks fine").ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        refused = ex;
                    }

                    AssertNotNull(refused, "An operator cannot release a PASS whose Checks did not pass at the reviewed commit");
                    Mission? still = await testDb.Driver.Missions.ReadAsync(held.Judge.Id).ConfigureAwait(false);
                    AssertTrue(JudgeCheckWaitHold.IsHeld(still), "The PASS stays held");
                    AssertEqual(0, held.Landed.LandedMissionIds.Count, "Nothing lands");
                }
            });

            await RunTest("A held Judge PASS is rejected when a Check fails at the reviewed commit, not re-run", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    CheckHeldJudgeScenario held = await CompleteJudgeWaitingOnChecksAsync(testDb).ConfigureAwait(false);
                    AssertTrue(JudgeCheckWaitHold.IsHeld(await testDb.Driver.Missions.ReadAsync(held.Judge.Id).ConfigureAwait(false)),
                        "Fixture: the PASS waits in the check-wait hold");

                    await SetCheckAsync(testDb, held.UnitTest.Id, CheckRunStatusEnum.Failed, _JudgeReviewedCommit).ConfigureAwait(false);
                    AssertEqual(1, await held.Landed.Scenario.Missions.ReleaseJudgeCheckWaitHoldsAsync().ConfigureAwait(false),
                        "A failed Check decides the held PASS");

                    Mission? rejected = await testDb.Driver.Missions.ReadAsync(held.Judge.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Failed, rejected!.Status, "The PASS is rejected");
                    AssertContains("Judge PASS rejected: an independent Check failed", rejected.FailureReason ?? String.Empty, "The reason names the gate");
                    AssertContains(held.UnitTest.Id, rejected.FailureReason ?? String.Empty, "The reason names the failed Check");
                    AssertFalse(rejected.HeldForOperatorReview, "A rejected PASS is no longer held");
                    AssertEqual(0, held.Landed.LandedMissionIds.Count, "A rejected PASS never lands");
                }
            });

            await RunTest("A held Judge PASS whose Checks never resolve is rejected after the wait budget", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    CheckHeldJudgeScenario held = await CompleteJudgeWaitingOnChecksAsync(testDb).ConfigureAwait(false);
                    MissionService missions = held.Landed.Scenario.Missions;
                    AssertEqual(0, await missions.ReleaseJudgeCheckWaitHoldsAsync().ConfigureAwait(false), "Inside the budget the PASS keeps waiting");

                    DateTime later = DateTime.UtcNow + missions.JudgeCheckWaitBudget + TimeSpan.FromMinutes(1);
                    missions.UtcNowProvider = () => later;
                    AssertEqual(1, await missions.ReleaseJudgeCheckWaitHoldsAsync().ConfigureAwait(false), "Past the budget the PASS is decided");

                    Mission? rejected = await testDb.Driver.Missions.ReadAsync(held.Judge.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Failed, rejected!.Status, "An unresolved PASS is rejected rather than held forever");
                    AssertContains("did not resolve within", rejected.FailureReason ?? String.Empty, "The reason names the wait budget");
                    AssertContains(held.UnitTest.Id, rejected.FailureReason ?? String.Empty, "The reason names the unresolved Check");
                    AssertEqual(0, held.Landed.LandedMissionIds.Count, "Nothing lands");
                }
            });

            await RunTest("A held Judge PASS whose reviewed commit changed gets a new Judge run", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync())
                {
                    CheckHeldJudgeScenario held = await CompleteJudgeWaitingOnChecksAsync(testDb).ConfigureAwait(false);
                    Mission moved = (await testDb.Driver.Missions.ReadAsync(held.Judge.Id).ConfigureAwait(false))!;
                    AssertTrue(JudgeCheckWaitHold.IsHeld(moved), "Fixture: the PASS waits in the check-wait hold");
                    moved.CommitHash = _OtherCommit;
                    await testDb.Driver.Missions.UpdateAsync(moved).ConfigureAwait(false);
                    await SetCheckAsync(testDb, held.UnitTest.Id, CheckRunStatusEnum.Passed, _JudgeReviewedCommit).ConfigureAwait(false);

                    AssertEqual(1, await held.Landed.Scenario.Missions.ReleaseJudgeCheckWaitHoldsAsync().ConfigureAwait(false), "The moved commit is decided");

                    Mission? rerun = await testDb.Driver.Missions.ReadAsync(held.Judge.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Pending, rerun!.Status, "A review of an older commit cannot decide the new one, so the Judge runs again");
                    AssertFalse(rerun.HeldForOperatorReview, "The re-run starts without the hold");
                    AssertEqual(0, held.Landed.LandedMissionIds.Count, "Nothing lands on a stale review");
                }
            });
        }

        private const string _JudgeReviewedCommit = "c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00";
        private const string _OtherCommit = "0badf00d0badf00d0badf00d0badf00d0badf00d";

        private sealed class CheckHeldJudgeScenario
        {
            public HeldJudgeScenario Landed { get; set; } = null!;
            public Mission Judge { get; set; } = null!;
            public CheckRun UnitTest { get; set; } = null!;
        }

        /// <summary>
        /// Runs a Worker and then a Judge that PASSes while its UnitTest Check at the reviewed commit is
        /// still running. The Build Check at that commit is green.
        /// </summary>
        private async Task<CheckHeldJudgeScenario> CompleteJudgeWaitingOnChecksAsync(TestDatabase testDb)
        {
            ReviewScenario scenario = await CreateScenarioAsync(testDb.Driver, includeDownstreamStage: true, workerRequiresReview: false).ConfigureAwait(false);
            HeldJudgeScenario landed = new HeldJudgeScenario { Scenario = scenario };
            scenario.Missions.OnMissionComplete = async (mission, dock) =>
            {
                landed.LandedMissionIds.Add(mission.Id);
                mission.Status = MissionStatusEnum.Complete;
                mission.CompletedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
            };

            await scenario.Missions.HandleCompletionAsync(scenario.WorkerCaptain, scenario.WorkerMission.Id).ConfigureAwait(false);
            await scenario.Admiral.WhenQueuedAssignmentsDrainedAsync().ConfigureAwait(false);

            Mission judge = await testDb.Driver.Missions.ReadAsync(scenario.DownstreamMission!.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Expected the Judge mission after handoff.");
            AssertEqual(MissionStatusEnum.InProgress, judge.Status, "Fixture: the Judge stage is running: " + judge.FailureReason);
            Captain judgeCaptain = await testDb.Driver.Captains.ReadAsync(judge.CaptainId!).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Expected the Judge captain.");

            judge.CommitHash = _JudgeReviewedCommit;
            await testDb.Driver.Missions.UpdateAsync(judge).ConfigureAwait(false);

            EnumerationResult<CheckRun> armed = await testDb.Driver.CheckRuns
                .EnumerateAsync(new CheckRunQuery { VoyageId = scenario.Voyage.Id, PageSize = 100 }).ConfigureAwait(false);
            foreach (CheckRun run in armed.Objects)
                await testDb.Driver.CheckRuns.DeleteAsync(run.Id).ConfigureAwait(false);

            await AddVoyageCheckAsync(testDb, scenario.Voyage.Id, CheckRunTypeEnum.Build, CheckRunStatusEnum.Passed, _JudgeReviewedCommit).ConfigureAwait(false);
            CheckRun unitTest = await AddVoyageCheckAsync(testDb, scenario.Voyage.Id, CheckRunTypeEnum.UnitTest, CheckRunStatusEnum.Running, _JudgeReviewedCommit).ConfigureAwait(false);

            string narrative = "The change covers every acceptance item and the tests exercise the primary and negative paths with specifics.";
            scenario.Missions.OnGetMissionOutput = _ =>
                "## Completeness\n" + narrative + "\n## Correctness\n" + narrative + "\n## Tests\n" + narrative
                + "\n## Failure Modes\n" + narrative + "\n## Verdict\nPASS\n[ARMADA:VERDICT] PASS";

            await scenario.Missions.HandleCompletionAsync(judgeCaptain, judge.Id).ConfigureAwait(false);
            landed.Judge = judge;
            return new CheckHeldJudgeScenario { Landed = landed, Judge = judge, UnitTest = unitTest };
        }

        private static async Task<CheckRun> AddVoyageCheckAsync(TestDatabase testDb, string voyageId, CheckRunTypeEnum type, CheckRunStatusEnum status, string commit)
        {
            CheckRun run = new CheckRun
            {
                VoyageId = voyageId,
                Label = type.ToString(),
                Type = type,
                Source = CheckRunSourceEnum.Armada,
                Status = status,
                Command = type == CheckRunTypeEnum.Build ? "dotnet build" : "dotnet test",
                WorkingDirectory = Path.GetTempPath(),
                CommitHash = commit,
                StartedUtc = DateTime.UtcNow,
                CompletedUtc = status == CheckRunStatusEnum.Running ? null : DateTime.UtcNow,
                ExitCode = status == CheckRunStatusEnum.Passed ? 0 : (status == CheckRunStatusEnum.Failed ? 1 : null),
                Summary = "check"
            };
            return await testDb.Driver.CheckRuns.CreateAsync(run).ConfigureAwait(false);
        }

        private static async Task SetCheckAsync(TestDatabase testDb, string checkRunId, CheckRunStatusEnum status, string commit)
        {
            CheckRun run = await testDb.Driver.CheckRuns.ReadAsync(checkRunId).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Expected the Check.");
            run.Status = status;
            run.CommitHash = commit;
            run.CompletedUtc = DateTime.UtcNow;
            run.ExitCode = status == CheckRunStatusEnum.Passed ? 0 : 1;
            run.Output = status == CheckRunStatusEnum.Passed ? "tests passed" : "tests failed";
            await testDb.Driver.CheckRuns.UpdateAsync(run).ConfigureAwait(false);
        }

        /// <summary>Marks the voyage record Cancelled, as an operator cancel leaves it, and returns the cancel time.</summary>
        private static async Task<DateTime> CancelVoyageRecordAsync(TestDatabase testDb, string voyageId)
        {
            Voyage voyage = await testDb.Driver.Voyages.ReadAsync(voyageId).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Expected the voyage.");
            DateTime cancelledUtc = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            voyage.Status = VoyageStatusEnum.Cancelled;
            voyage.CompletedUtc = cancelledUtc;
            voyage.LastUpdateUtc = cancelledUtc;
            await testDb.Driver.Voyages.UpdateAsync(voyage).ConfigureAwait(false);
            return cancelledUtc;
        }

        private sealed class HeldJudgeScenario
        {
            public ReviewScenario Scenario { get; set; } = null!;
            public Mission Judge { get; set; } = null!;
            public List<string> LandedMissionIds { get; } = new List<string>();
        }

        private async Task<HeldJudgeScenario> CompleteHeldJudgeAsync(TestDatabase testDb)
        {
            ReviewScenario scenario = await CreateScenarioAsync(testDb.Driver, includeDownstreamStage: true, workerRequiresReview: false).ConfigureAwait(false);
            HeldJudgeScenario held = new HeldJudgeScenario { Scenario = scenario };
            scenario.Missions.OnMissionComplete = async (mission, dock) =>
            {
                held.LandedMissionIds.Add(mission.Id);
                mission.Status = MissionStatusEnum.Complete;
                mission.CompletedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await testDb.Driver.Missions.UpdateAsync(mission).ConfigureAwait(false);
            };

            await scenario.Missions.HandleCompletionAsync(scenario.WorkerCaptain, scenario.WorkerMission.Id).ConfigureAwait(false);
            await scenario.Admiral.WhenQueuedAssignmentsDrainedAsync().ConfigureAwait(false);

            Mission judge = await testDb.Driver.Missions.ReadAsync(scenario.DownstreamMission!.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Expected the Judge mission after handoff.");
            AssertEqual(MissionStatusEnum.InProgress, judge.Status, "Fixture: the Judge stage is running: " + judge.FailureReason);
            Captain judgeCaptain = await testDb.Driver.Captains.ReadAsync(judge.CaptainId!).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Expected the Judge captain.");

            // Checks armed at dispatch would hold the PASS until they run; this fixture judges the PASS with
            // the documented no-Checks exclusion so the review-substance decision is what is exercised.
            EnumerationResult<CheckRun> armed = await testDb.Driver.CheckRuns
                .EnumerateAsync(new CheckRunQuery { VoyageId = scenario.Voyage.Id, PageSize = 100 }).ConfigureAwait(false);
            foreach (CheckRun run in armed.Objects)
                await testDb.Driver.CheckRuns.DeleteAsync(run.Id).ConfigureAwait(false);

            string narrative = "The change covers every acceptance item and the tests exercise the primary and negative paths with specifics.";
            scenario.Missions.OnGetMissionOutput = _ =>
                "## Completeness\n" + narrative + "\n## Correctness\n" + narrative + "\n## Tests\n" + narrative
                + "\n## Failure Modes\n" + narrative + "\n## Verdict\nPASS\n[JUDGE-CHECK-EXCLUSION] no independent Checks in this fixture\n[ARMADA:VERDICT] PASS";

            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
            double[] sections = new double[] { 0.30, 0.20, 0.10, 0.20 };
            for (int i = 0; i < sections.Length; i++)
                answers["section_" + (i + 1)] = new TypedAnswer { Type = "noul", Noul = sections[i], Confidence = sections[i] };
            answers["substantiated"] = new TypedAnswer { Type = "score", Score = 0.0, Confidence = 0.97 };
            TypedDecisionSettings typedSettings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            typedSettings.Decisions["review_substance"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.85 };
            scenario.Missions.ReviewSubstanceAdapter = new TypedReviewSubstanceAdapter(
                new FakeTypedDecisionClient(new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 }),
                new TypedDecisionRecorder(testDb.Driver, CreateLogging()),
                typedSettings,
                CreateLogging());

            await scenario.Missions.HandleCompletionAsync(judgeCaptain, judge.Id).ConfigureAwait(false);
            held.Judge = judge;
            return held;
        }

        private async Task<ReviewScenario> CreateScenarioAsync(
            SqliteDatabaseDriver db,
            bool includeDownstreamStage,
            ReviewDenyActionEnum firstStageDenyAction = ReviewDenyActionEnum.RetryStage,
            bool workerRequiresReview = true)
        {
            LoggingModule logging = CreateLogging();
            ArmadaSettings settings = CreateSettings();
            DirCreatingGitStub git = new DirCreatingGitStub();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            MissionService missionService = new MissionService(logging, db, settings, dockService, captainService, git: git, resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
            IVoyageService voyageService = new VoyageService(logging, db);
            AdmiralService admiralService = new AdmiralService(logging, db, settings, captainService, missionService, voyageService, dockService);

            int nextPid = 4000;
            captainService.OnLaunchAgent = (_, _, _) =>
            {
                nextPid++;
                return Task.FromResult(nextPid);
            };
            missionService.OnGetMissionOutput = _ =>
                "[ARMADA:RESULT] COMPLETE\nImplemented the requested behavior with unit test coverage for the primary and negative paths. The change is committed to the mission branch and the suite passes locally with no regressions in the affected modules.";

            Vessel vessel = new Vessel("review-vessel", "https://github.com/test/repo.git");
            vessel.LocalPath = Path.Combine(Path.GetTempPath(), "armada_review_bare_" + Guid.NewGuid().ToString("N"));
            vessel.WorkingDirectory = Path.Combine(Path.GetTempPath(), "armada_review_work_" + Guid.NewGuid().ToString("N"));
            _FixtureDirectories.Add(vessel.LocalPath);
            _FixtureDirectories.Add(vessel.WorkingDirectory);
            vessel.DefaultBranch = "main";
            vessel = await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Captain workerCaptain = new Captain("review-worker");
            workerCaptain.State = CaptainStateEnum.Idle;
            workerCaptain.AllowedPersonas = "[\"Worker\"]";
            workerCaptain = await db.Captains.CreateAsync(workerCaptain).ConfigureAwait(false);

            Captain? judgeCaptain = null;
            if (includeDownstreamStage)
            {
                judgeCaptain = new Captain("review-judge");
                judgeCaptain.State = CaptainStateEnum.Idle;
                judgeCaptain.AllowedPersonas = "[\"Judge\"]";
                judgeCaptain = await db.Captains.CreateAsync(judgeCaptain).ConfigureAwait(false);
            }

            Pipeline pipeline = new Pipeline(includeDownstreamStage ? "ReviewedPipeline" : "ReviewedWorkerOnly");
            pipeline.Stages = new List<PipelineStage>
            {
                new PipelineStage(1, "Worker")
                {
                    RequiresReview = workerRequiresReview,
                    ReviewDenyAction = firstStageDenyAction
                }
            };
            if (includeDownstreamStage)
            {
                pipeline.Stages.Add(new PipelineStage(2, "Judge"));
            }

            pipeline = await db.Pipelines.CreateAsync(pipeline).ConfigureAwait(false);

            Voyage voyage = await admiralService.DispatchVoyageAsync(
                "Review Voyage",
                "Review workflow coverage",
                vessel.Id,
                new List<MissionDescription>
                {
                    new MissionDescription("Implement review gate", "Implement the requested change.")
                },
                pipeline.Id).ConfigureAwait(false);

            List<Mission> voyageMissions = await db.Missions.EnumerateByVoyageAsync(voyage.Id).ConfigureAwait(false);
            Mission workerMission = voyageMissions.First(m => String.Equals(m.Persona ?? "Worker", "Worker", StringComparison.OrdinalIgnoreCase));
            Mission? downstreamMission = voyageMissions.FirstOrDefault(m => String.Equals(m.Persona, "Judge", StringComparison.OrdinalIgnoreCase));

            // Dispatch assigns in background work that also visits the downstream stage. A scenario
            // that proceeds while that work runs races it for the sibling-lane lease and the mission
            // row, so the review action under test can lose its own assignment. Wait for it to end.
            await admiralService.WhenQueuedAssignmentsDrainedAsync().ConfigureAwait(false);

            workerMission = await db.Missions.ReadAsync(workerMission.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Expected worker mission after dispatch.");
            Captain? assignedWorker = await db.Captains.ReadAsync(workerCaptain.Id).ConfigureAwait(false);

            if (includeDownstreamStage)
            {
                AssertNotNull(downstreamMission, "Downstream judge stage should exist");
            }

            AssertEqual(MissionStatusEnum.InProgress, workerMission.Status, "Fixture assignment: " + workerMission.FailureReason);
            AssertNotNull(workerMission.DockId, "Fixture mission dock");
            AssertEqual(MissionAssignmentStateEnum.Assigned, workerMission.AssignmentState, "Fixture assignment state");
            AssertNotNull(workerMission.ProcessId, "Fixture mission process");
            AssertEqual(workerMission.Id, assignedWorker?.CurrentMissionId, "Fixture captain holds the worker mission");
            AssertNotNull(assignedWorker!.ProcessId, "Fixture captain process");

            return new ReviewScenario
            {
                Missions = missionService,
                Admiral = admiralService,
                Vessel = vessel,
                Voyage = voyage,
                WorkerCaptain = assignedWorker!,
                JudgeCaptain = judgeCaptain,
                WorkerMission = workerMission,
                DownstreamMission = downstreamMission
            };
        }

        private sealed class ReviewScenario
        {
            public MissionService Missions { get; set; } = null!;
            public AdmiralService Admiral { get; set; } = null!;
            public Vessel Vessel { get; set; } = null!;
            public Voyage Voyage { get; set; } = null!;
            public Captain WorkerCaptain { get; set; } = null!;
            public Captain? JudgeCaptain { get; set; } = null;
            public Mission WorkerMission { get; set; } = null!;
            public Mission? DownstreamMission { get; set; } = null;
        }

        /// <summary>
        /// Git stub that creates worktree directories so mission instructions can be written.
        /// </summary>
        private sealed class DirCreatingGitStub : IGitService
        {
            private readonly HashSet<string> _Branches = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "main" };

            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default) => Task.CompletedTask;

            public Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", bool detached = false, CancellationToken token = default)
            {
                Directory.CreateDirectory(worktreePath);
                _Branches.Add(branchName);
                return Task.CompletedTask;
            }

            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task FetchAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => Task.CompletedTask;
            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default) => Task.CompletedTask;
            public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default) => Task.FromResult("refs/heads/main");
            public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default)
                => Task.FromResult("https://github.com/test/repo/pull/1");
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;
            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(true);
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default, [System.Runtime.CompilerServices.CallerMemberName] string caller = "") => Task.CompletedTask;
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default, [System.Runtime.CompilerServices.CallerMemberName] string caller = "") => Task.CompletedTask;
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => Task.CompletedTask;
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;
            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult<string?>("main");
            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> HasUncommittedTrackedChangesAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(false);
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => Task.FromResult(String.Empty);
            /// <summary>
            /// Files the simulated captain changed since its dock was provisioned. A producing stage in
            /// these pipelines stands for a captain that committed, so one changed file is the default.
            /// </summary>
            public IReadOnlyList<string> ChangedFilesSinceResult { get; set; } = new string[] { "src/Simulated/Change.cs" };

            /// <inheritdoc />
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default)
                => Task.FromResult(ChangedFilesSinceResult);
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(true);
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => Task.FromResult<string?>("abc123def456");
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default)
                => Task.FromResult(_Branches.Contains(branchName));
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default)
                => BranchExistsAsync(repoPath, branchName, token);
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(false);
            public Task<IReadOnlyList<string>> GetConflictedFilesAsync(string worktreePath, CancellationToken token = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default) => Task.FromResult(0);
            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => Task.CompletedTask;
        }
    }
}
