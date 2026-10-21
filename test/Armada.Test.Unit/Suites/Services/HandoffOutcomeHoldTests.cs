namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
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
    /// The <c>handoff_outcome</c> hold at the mission service boundary. A gated partial outcome at or
    /// above 0.90 with the hold on holds the next stage and lists the unmet criteria; the operator's
    /// release, mail_release and rescue choices each end it and record a label joined to the decision on
    /// its state digest; an unanswered hold is released with the Mail at its timeout. With the hold off,
    /// below 0.90, or with the decision unavailable, the handoff is the one the decision makes without a
    /// hold. The hold itself never cancels, rescues, lands or dispatches.
    /// </summary>
    public sealed class HandoffOutcomeHoldTests : TestSuite
    {
        #region Public-Members

        /// <summary>Suite name.</summary>
        public override string Name => "Handoff Outcome Hold";

        #endregion

        #region Private-Members

        private const string _WorkerBranch = "armada/worker-partial";
        private const string _MetCriterion = "The report lists every exported symbol.";
        private const string _UnmetCriterion = "A test fails without the change.";
        private const string _JudgeBrief = "Review the worker's change.";

        private static readonly DateTime _HoldStart = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        #endregion

        #region Protected-Methods

        /// <summary>Run the suite.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Confident partial with the hold on holds the next stage and lists the unmet criteria", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HoldScenario scenario = await CreateScenarioAsync(testDb.Driver, true, PartialResult(0.95)).ConfigureAwait(false);
                    Captain idle = await CreateIdleJudgeCaptainAsync(testDb.Driver).ConfigureAwait(false);

                    await scenario.Missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);

                    Mission worker = await ReadAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false);
                    Mission judge = await ReadAsync(testDb.Driver, scenario.Judge.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.WorkProduced, worker.Status, "the held stage keeps its produced work");
                    AssertTrue(worker.HeldForOperatorReview, "the stage is held for an operator choice");
                    AssertStartsWith("handoff_outcome_hold since ", worker.HeldForOperatorReviewReason ?? String.Empty, "the hold reason names the hold");
                    AssertContains(_UnmetCriterion, worker.HeldForOperatorReviewReason ?? String.Empty, "the reason lists the unmet criterion");
                    AssertFalse((worker.HeldForOperatorReviewReason ?? String.Empty).Contains(_MetCriterion, StringComparison.Ordinal), "a met criterion is not listed");

                    AssertEqual(MissionStatusEnum.Pending, judge.Status, "the hold cancels nothing");
                    AssertNull(judge.BranchName, "the next stage is not handed the branch while held");
                    AssertEqual(_JudgeBrief, judge.Description, "the next stage's brief is not Mailed while held");
                    AssertNull(judge.CaptainId, "the hold dispatches nothing");
                    Captain idleAfter = (await testDb.Driver.Captains.ReadAsync(idle.Id).ConfigureAwait(false))!;
                    AssertEqual(CaptainStateEnum.Idle, idleAfter.State, "no captain is launched by the hold");
                    AssertEqual(2, (await testDb.Driver.Missions.EnumerateByVoyageAsync(scenario.Voyage.Id).ConfigureAwait(false)).Count,
                        "the hold creates no rescue or other mission");
                    AssertEqual(1, await CountEventsAsync(testDb.Driver, worker.Id, "mission.handoff_hold_engaged").ConfigureAwait(false), "the hold records that it engaged");
                    AssertEqual(0, await CountEventsAsync(testDb.Driver, worker.Id, "typed_decision.labelled").ConfigureAwait(false), "no label until the hold ends");

                    AssertEqual(0, await scenario.Missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false), "a held stage is not re-driven by the dangling-handoff sweep");

                    InboxService inbox = new InboxService(testDb.Driver, Quiet());
                    List<InboxItem> items = await inbox.GetInboxAsync().ConfigureAwait(false);
                    InboxItem? item = items.FirstOrDefault(entry => entry.Kind == "handoff_outcome_hold" && entry.EntityId == worker.Id);
                    AssertNotNull(item, "the operator inbox lists the held handoff");
                    AssertContains(_UnmetCriterion, item!.Detail, "the inbox item shows the unmet criteria");
                    AssertContains("armada_handoff_hold", item.Detail, "the inbox item names the operator tool");
                    AssertFalse(items.Any(entry => entry.Kind == "judge_pass_held" && entry.EntityId == worker.Id), "it is not listed as a held Judge PASS");

                    InvalidOperationException? refused = null;
                    try
                    {
                        await scenario.Missions.ClearOperatorReviewHoldAsync(worker.Id, "operator-a", "clear it").ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        refused = ex;
                    }
                    AssertNotNull(refused, "the Judge-hold clear refuses a handoff hold");
                    AssertStartsWith("handoff_outcome_hold:", refused!.Message, "the refusal names the hold");
                    AssertTrue((await ReadAsync(testDb.Driver, worker.Id).ConfigureAwait(false)).HeldForOperatorReview, "the refused clear leaves the hold in place");
                }
            });

            await RunTest("Release hands the stage off unchanged and records the release label", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HoldScenario scenario = await CreateHeldScenarioAsync(testDb.Driver).ConfigureAwait(false);

                    Mission released = await scenario.Missions.ResolveHandoffOutcomeHoldAsync(
                        scenario.Worker.Id, HandoffOutcomeHoldChoiceEnum.Release, "operator-a", "the gap is covered by the next stage").ConfigureAwait(false);

                    Mission judge = await ReadAsync(testDb.Driver, scenario.Judge.Id).ConfigureAwait(false);
                    AssertFalse(released.HeldForOperatorReview, "the hold is cleared");
                    AssertEqual(_WorkerBranch, judge.BranchName, "the next stage is handed the branch");
                    AssertFalse((judge.Description ?? String.Empty).Contains("handoff-outcome check", StringComparison.Ordinal), "release adds no unmet-criteria note");

                    LabelPayload label = await ReadSingleLabelAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false);
                    AssertEqual("release", label.Label, "the choice is labelled release");
                    AssertEqual("operator-a", label.DecidedBy, "the label names the operator");
                    AssertEqual("handoff_outcome", label.Decision, "the label names the decision");
                    AssertEqual(await ReadGatedStateDigestAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false), label.StateSha256,
                        "the label carries the decision's state digest");
                }
            });

            await RunTest("Mail and release Mails the unmet criteria, hands off, and records the mail_release label", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HoldScenario scenario = await CreateHeldScenarioAsync(testDb.Driver).ConfigureAwait(false);

                    await scenario.Missions.ResolveHandoffOutcomeHoldAsync(
                        scenario.Worker.Id, HandoffOutcomeHoldChoiceEnum.MailAndRelease, "operator-b", "tell the reviewer").ConfigureAwait(false);

                    Mission judge = await ReadAsync(testDb.Driver, scenario.Judge.Id).ConfigureAwait(false);
                    AssertEqual(_WorkerBranch, judge.BranchName, "the next stage is handed the branch");
                    AssertContains("handoff-outcome check", judge.Description ?? String.Empty, "the next brief carries the note");
                    AssertContains(_UnmetCriterion, judge.Description ?? String.Empty, "the note lists the unmet criterion");
                    AssertFalse((judge.Description ?? String.Empty).Contains(_MetCriterion, StringComparison.Ordinal), "the note leaves out the met criterion");

                    LabelPayload label = await ReadSingleLabelAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false);
                    AssertEqual("mail_release", label.Label, "the choice is labelled mail_release");
                    AssertEqual("operator-b", label.DecidedBy, "the label names the operator");
                    AssertEqual(await ReadGatedStateDigestAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false), label.StateSha256,
                        "the label carries the decision's state digest");
                }
            });

            await RunTest("Rescue fails the held stage, cancels the next stage, and records the rescue label", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HoldScenario scenario = await CreateHeldScenarioAsync(testDb.Driver).ConfigureAwait(false);

                    Mission rescued = await scenario.Missions.ResolveHandoffOutcomeHoldAsync(
                        scenario.Worker.Id, HandoffOutcomeHoldChoiceEnum.Rescue, "operator-c", "redo the missing test").ConfigureAwait(false);

                    Mission worker = await ReadAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false);
                    Mission judge = await ReadAsync(testDb.Driver, scenario.Judge.Id).ConfigureAwait(false);
                    AssertEqual(MissionStatusEnum.Failed, worker.Status, "the held stage fails so recovery can decide a rescue");
                    AssertFalse(worker.HeldForOperatorReview, "the hold is cleared");
                    AssertStartsWith("handoff_outcome_hold_rescue:", worker.FailureReason ?? String.Empty, "the failure names the rescue choice");
                    AssertContains("criteria 2 ", worker.FailureReason ?? String.Empty, "the failure names the unmet criterion index");
                    AssertFalse((worker.FailureReason ?? String.Empty).Contains("redo the missing test", StringComparison.Ordinal),
                        "the operator's words stay on the label, not on the failure recovery reads");
                    AssertEqual(MissionStatusEnum.Cancelled, judge.Status, "the next stage is cancelled");
                    AssertEqual(2, (await testDb.Driver.Missions.EnumerateByVoyageAsync(scenario.Voyage.Id).ConfigureAwait(false)).Count,
                        "the choice itself starts no rescue mission");
                    AssertEqual(MissionStatusEnum.Failed, rescued.Status);

                    LabelPayload label = await ReadSingleLabelAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false);
                    AssertEqual("rescue", label.Label, "the choice is labelled rescue");
                    AssertEqual("operator-c", label.DecidedBy, "the label names the operator");
                    AssertEqual("redo the missing test", label.Reason, "the label keeps the operator's reason");
                }
            });

            await RunTest("An unanswered hold is released with the Mail at its timeout and not before", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HoldScenario scenario = await CreateHeldScenarioAsync(testDb.Driver).ConfigureAwait(false);
                    Captain idle = await CreateIdleJudgeCaptainAsync(testDb.Driver).ConfigureAwait(false);

                    scenario.Missions.UtcNowProvider = () => _HoldStart.AddMinutes(29);
                    AssertEqual(0, await scenario.Missions.ReleaseExpiredHandoffOutcomeHoldsAsync().ConfigureAwait(false), "a hold inside its timeout stays");
                    AssertTrue((await ReadAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false)).HeldForOperatorReview, "still held at 29 of 30 minutes");

                    scenario.Missions.UtcNowProvider = () => _HoldStart.AddMinutes(31);
                    AssertEqual(1, await scenario.Missions.ReleaseExpiredHandoffOutcomeHoldsAsync().ConfigureAwait(false), "a hold past its timeout is released");

                    Mission worker = await ReadAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false);
                    Mission judge = await ReadAsync(testDb.Driver, scenario.Judge.Id).ConfigureAwait(false);
                    AssertFalse(worker.HeldForOperatorReview, "the hold is cleared");
                    AssertEqual(MissionStatusEnum.WorkProduced, worker.Status, "the timeout fails nothing");
                    AssertEqual(_WorkerBranch, judge.BranchName, "the next stage is handed the branch");
                    AssertContains("handoff-outcome check", judge.Description ?? String.Empty, "the timeout Mails the unmet-criteria note");
                    AssertContains(_UnmetCriterion, judge.Description ?? String.Empty, "the note lists the unmet criterion");
                    AssertEqual(MissionStatusEnum.Pending, judge.Status, "the timeout cancels nothing");
                    AssertNull(judge.CaptainId, "the timeout dispatches nothing");
                    AssertEqual(CaptainStateEnum.Idle, (await testDb.Driver.Captains.ReadAsync(idle.Id).ConfigureAwait(false))!.State, "no captain is launched by the timeout");

                    LabelPayload label = await ReadSingleLabelAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false);
                    AssertEqual("hold_timeout", label.Label, "the timeout is labelled hold_timeout");
                    AssertEqual("timeout", label.DecidedBy, "the label says no operator decided");
                    AssertEqual(await ReadGatedStateDigestAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false), label.StateSha256,
                        "the label carries the decision's state digest");
                }
            });

            await RunTest("Hold off, below 0.90, or decision unavailable keeps the handoff without a hold", async () =>
            {
                List<FailClosedCase> cases = new List<FailClosedCase>
                {
                    new FailClosedCase("hold off", false, PartialResult(0.95), true),
                    new FailClosedCase("partial below 0.90", true, PartialResult(0.87), true),
                    new FailClosedCase("decision unavailable", true, FakeTypedDecisionClient.Unavailable("timeout"), false)
                };

                foreach (FailClosedCase entry in cases)
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        HoldScenario scenario = await CreateScenarioAsync(testDb.Driver, entry.HoldEnabled, entry.Result).ConfigureAwait(false);

                        await scenario.Missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);

                        Mission worker = await ReadAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false);
                        Mission judge = await ReadAsync(testDb.Driver, scenario.Judge.Id).ConfigureAwait(false);
                        AssertFalse(worker.HeldForOperatorReview, entry.Name + ": the stage is not held");
                        AssertEqual(_WorkerBranch, judge.BranchName, entry.Name + ": the next stage is handed the branch");
                        AssertEqual(entry.ExpectMail, (judge.Description ?? String.Empty).Contains("handoff-outcome check", StringComparison.Ordinal),
                            entry.Name + ": the unmet-criteria Mail is sent exactly when the decision reads partial");
                        AssertEqual(0, await CountEventsAsync(testDb.Driver, worker.Id, "mission.handoff_hold_engaged").ConfigureAwait(false), entry.Name + ": no hold engaged");
                        AssertEqual(0, await CountEventsAsync(testDb.Driver, worker.Id, "typed_decision.labelled").ConfigureAwait(false), entry.Name + ": no label");
                    }
                }
            });

            await RunTest("A confident partial on a test, lint or record stage Mails the next stage and never holds it", async () =>
            {
                foreach (string persona in new[] { "Test Engineer", "TestEngineer", "Linter", "Recorder" })
                {
                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        HoldScenario scenario = await CreateScenarioAsync(testDb.Driver, true, PartialResult(0.95), persona).ConfigureAwait(false);

                        await scenario.Missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);

                        Mission finished = await ReadAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false);
                        Mission judge = await ReadAsync(testDb.Driver, scenario.Judge.Id).ConfigureAwait(false);
                        AssertFalse(finished.HeldForOperatorReview, persona + ": the stage is not held");
                        AssertEqual(_WorkerBranch, judge.BranchName, persona + ": the next stage is handed the branch");
                        AssertContains("handoff-outcome check", judge.Description ?? String.Empty, persona + ": the unmet-criteria Mail is sent");
                        AssertEqual(0, await CountEventsAsync(testDb.Driver, finished.Id, "mission.handoff_hold_engaged").ConfigureAwait(false), persona + ": no hold engaged");
                    }
                }
            });

            await RunTest("armada_handoff_hold refuses a non-administrator and resolves a hold for an operator", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    HoldScenario scenario = await CreateHeldScenarioAsync(testDb.Driver).ConfigureAwait(false);

                    const string toolName = "armada_handoff_hold";
                    Func<JsonElement?, Task<object>>? handler = null;
                    Armada.Server.Mcp.Tools.McpHandoffHoldTools.Register(
                        (name, _, _, registered) => { if (name == toolName) handler = registered; },
                        scenario.Missions);
                    AssertNotNull(handler, toolName + " is registered");

                    AuthContext tenantAdmin = AuthContext.Authenticated(Armada.Core.Constants.DefaultTenantId, "usr_hold_admin", false, true, "Test");
                    AssertFalse(Armada.Server.Mcp.McpToolAccessPolicy.IsAllowed(tenantAdmin, toolName), "a narrower caller cannot reach the tool");
                    string refusedJson;
                    using (Armada.Server.Mcp.McpCallerContext.Begin(tenantAdmin))
                    {
                        refusedJson = JsonSerializer.Serialize(await handler!(JsonSerializer.SerializeToElement(
                            new { action = "release", missionId = scenario.Worker.Id, reason = "go", @operator = "someone" })).ConfigureAwait(false));
                    }
                    AssertContains("global_administrator_required", refusedJson, "a non-administrator is refused");
                    AssertTrue((await ReadAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false)).HeldForOperatorReview, "a refused call leaves the hold");

                    string invalidJson = JsonSerializer.Serialize(await McpTestCaller.Wrap(handler!)(JsonSerializer.SerializeToElement(
                        new { action = "land", missionId = scenario.Worker.Id, reason = "go", @operator = "operator-a" })).ConfigureAwait(false));
                    AssertContains("invalid_action", invalidJson, "only release, mail_release and rescue are accepted");

                    string releasedJson = JsonSerializer.Serialize(await McpTestCaller.Wrap(handler!)(JsonSerializer.SerializeToElement(
                        new { action = "mail_release", missionId = scenario.Worker.Id, reason = "tell the reviewer", @operator = "operator-a" })).ConfigureAwait(false));
                    AssertFalse(releasedJson.Contains("\"Error\"", StringComparison.Ordinal), "the operator choice succeeds: " + releasedJson);
                    AssertEqual("mail_release", (await ReadSingleLabelAsync(testDb.Driver, scenario.Worker.Id).ConfigureAwait(false)).Label, "the tool records the label");

                    string againJson = JsonSerializer.Serialize(await McpTestCaller.Wrap(handler!)(JsonSerializer.SerializeToElement(
                        new { action = "release", missionId = scenario.Worker.Id, reason = "again", @operator = "operator-a" })).ConfigureAwait(false));
                    AssertContains("not_held", againJson, "a released hold cannot be chosen twice");
                }
            });
        }

        #endregion

        #region Private-Methods

        private static LoggingModule Quiet()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static ArmadaSettings CreateSettings(bool holdEnabled)
        {
            string id = Guid.NewGuid().ToString("N");
            ArmadaSettings settings = new ArmadaSettings();
            settings.DocksDirectory = Path.Combine(Path.GetTempPath(), "armada_handoffhold_docks_" + id);
            settings.ReposDirectory = Path.Combine(Path.GetTempPath(), "armada_handoffhold_repos_" + id);
            settings.LogDirectory = Path.Combine(Path.GetTempPath(), "armada_handoffhold_logs_" + id);
            settings.TypedDecisions.Mode = TypedDecisionModeEnum.Gate;
            settings.TypedDecisions.Decisions["handoff_outcome"] = new TypedDecisionRuleSettings { Mode = TypedDecisionModeEnum.Gate, GateThreshold = 0.85 };
            settings.TypedDecisions.HandoffOutcomeHold.Enabled = holdEnabled;
            settings.TypedDecisions.HandoffOutcomeHold.TimeoutMinutes = 30;
            return settings;
        }

        /// <summary>
        /// A gated <c>partial</c> reading: criterion 1 reads met and criterion 2 reads unmet.
        /// </summary>
        private static TypedDecisionResult PartialResult(double confidence)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                ["outcome"] = new TypedAnswer { Type = "choice", Choice = "partial", Confidence = confidence },
                ["next_stage_useful"] = new TypedAnswer { Type = "noul", Noul = 0.6, Confidence = 0.6 },
                ["gap_1"] = new TypedAnswer { Type = "noul", Noul = 0.9, Confidence = 0.9 },
                ["gap_2"] = new TypedAnswer { Type = "noul", Noul = 0.1, Confidence = 0.1 }
            };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        private static async Task<HoldScenario> CreateScenarioAsync(SqliteDatabaseDriver db, bool holdEnabled, TypedDecisionResult result, string finishedPersona = "Worker")
        {
            ArmadaSettings settings = CreateSettings(holdEnabled);
            LoggingModule logging = Quiet();
            StubGitService git = new StubGitService();
            IDockService dockService = new DockService(logging, db, settings, git);
            CaptainService captainService = new CaptainService(logging, db, settings, git, dockService);
            captainService.OnLaunchAgent = (_, _, _) => Task.FromResult(64020);
            MissionService missions = new MissionService(logging, db, settings, dockService, captainService,
                resourcePressureAdmission: TestResourcePressure.Unconstrained(settings));
            missions.UtcNowProvider = () => _HoldStart;
            missions.HandoffOutcomeAdapter = new TypedHandoffOutcomeAdapter(
                new FakeTypedDecisionClient(result), new TypedDecisionRecorder(db, logging), settings.TypedDecisions, logging);

            Vessel vessel = new Vessel("handoffhold-vessel-" + Guid.NewGuid().ToString("N"), "https://github.com/test/handoffhold.git");
            vessel.LocalPath = Path.Combine(settings.ReposDirectory, vessel.Name + ".git");
            vessel.DefaultBranch = "main";
            vessel = await db.Vessels.CreateAsync(vessel).ConfigureAwait(false);

            Voyage voyage = await db.Voyages.CreateAsync(new Voyage("Handoff hold voyage")).ConfigureAwait(false);
            await db.Objectives.CreateAsync(new Objective
            {
                Title = "Export report",
                VoyageIds = new List<string> { voyage.Id },
                AcceptanceCriteria = new List<string> { _MetCriterion, _UnmetCriterion }
            }).ConfigureAwait(false);

            Mission worker = new Mission("Write the export report", "Write the report.");
            worker.VesselId = vessel.Id;
            worker.VoyageId = voyage.Id;
            worker.Persona = finishedPersona;
            worker.BranchName = _WorkerBranch;
            worker.Status = MissionStatusEnum.WorkProduced;
            worker.AgentOutput = "Wrote the report; the failing test is not written yet.";
            worker = await db.Missions.CreateAsync(worker).ConfigureAwait(false);

            Mission judge = new Mission("Judge stage", _JudgeBrief);
            judge.VesselId = vessel.Id;
            judge.VoyageId = voyage.Id;
            judge.Persona = "Judge";
            judge.Status = MissionStatusEnum.Pending;
            judge.DependsOnMissionId = worker.Id;
            judge = await db.Missions.CreateAsync(judge).ConfigureAwait(false);

            return new HoldScenario(missions, voyage, worker, judge);
        }

        private async Task<HoldScenario> CreateHeldScenarioAsync(SqliteDatabaseDriver db)
        {
            HoldScenario scenario = await CreateScenarioAsync(db, true, PartialResult(0.95)).ConfigureAwait(false);
            await scenario.Missions.RecoverDanglingHandoffsAsync().ConfigureAwait(false);
            AssertTrue((await ReadAsync(db, scenario.Worker.Id).ConfigureAwait(false)).HeldForOperatorReview, "Fixture: the stage is held before the choice");
            return scenario;
        }

        private static async Task<Captain> CreateIdleJudgeCaptainAsync(SqliteDatabaseDriver db)
        {
            Captain captain = new Captain("handoffhold-judge-" + Guid.NewGuid().ToString("N"));
            captain.Model = "claude-opus-5";
            captain.AllowedPersonas = "[\"Judge\"]";
            captain.State = CaptainStateEnum.Idle;
            return await db.Captains.CreateAsync(captain).ConfigureAwait(false);
        }

        private static async Task<Mission> ReadAsync(SqliteDatabaseDriver db, string missionId)
        {
            Mission? mission = await db.Missions.ReadAsync(missionId).ConfigureAwait(false);
            if (mission == null) throw new InvalidOperationException("Fixture mission missing: " + missionId);
            return mission;
        }

        private static async Task<List<ArmadaEvent>> ReadEventsAsync(SqliteDatabaseDriver db, string missionId, string eventType)
        {
            EnumerationResult<ArmadaEvent> page = await db.Events.EnumerateAsync(new EnumerationQuery
            {
                MissionId = missionId,
                EventType = eventType,
                PageNumber = 1,
                PageSize = 100
            }).ConfigureAwait(false);
            return page.Objects;
        }

        private static async Task<int> CountEventsAsync(SqliteDatabaseDriver db, string missionId, string eventType)
        {
            return (await ReadEventsAsync(db, missionId, eventType).ConfigureAwait(false)).Count;
        }

        private async Task<LabelPayload> ReadSingleLabelAsync(SqliteDatabaseDriver db, string missionId)
        {
            List<ArmadaEvent> labels = await ReadEventsAsync(db, missionId, "typed_decision.labelled").ConfigureAwait(false);
            AssertEqual(1, labels.Count, "exactly one label is recorded");
            LabelPayload? payload = JsonSerializer.Deserialize<LabelPayload>(labels[0].Payload ?? "{}");
            AssertNotNull(payload, "the label payload reads");
            return payload!;
        }

        private async Task<string?> ReadGatedStateDigestAsync(SqliteDatabaseDriver db, string missionId)
        {
            List<ArmadaEvent> gated = await ReadEventsAsync(db, missionId, "typed_decision.gated").ConfigureAwait(false);
            AssertEqual(1, gated.Count, "Fixture: one gated decision");
            GatedPayload? payload = JsonSerializer.Deserialize<GatedPayload>(gated[0].Payload ?? "{}");
            AssertFalse(String.IsNullOrEmpty(payload?.StateSha256), "Fixture: the decision recorded a state digest");
            return payload!.StateSha256;
        }

        #endregion

        #region Private-Types

        private sealed class HoldScenario
        {
            public HoldScenario(MissionService missions, Voyage voyage, Mission worker, Mission judge)
            {
                Missions = missions;
                Voyage = voyage;
                Worker = worker;
                Judge = judge;
            }

            public MissionService Missions { get; }

            public Voyage Voyage { get; }

            public Mission Worker { get; }

            public Mission Judge { get; }
        }

        private sealed class FailClosedCase
        {
            public FailClosedCase(string name, bool holdEnabled, TypedDecisionResult result, bool expectMail)
            {
                Name = name;
                HoldEnabled = holdEnabled;
                Result = result;
                ExpectMail = expectMail;
            }

            public string Name { get; }

            public bool HoldEnabled { get; }

            public TypedDecisionResult Result { get; }

            public bool ExpectMail { get; }
        }

        private sealed class LabelPayload
        {
            [JsonPropertyName("decision")]
            public string? Decision { get; set; } = null;

            [JsonPropertyName("label")]
            public string? Label { get; set; } = null;

            [JsonPropertyName("decided_by")]
            public string? DecidedBy { get; set; } = null;

            [JsonPropertyName("reason")]
            public string? Reason { get; set; } = null;

            [JsonPropertyName("state_sha256")]
            public string? StateSha256 { get; set; } = null;
        }

        private sealed class GatedPayload
        {
            [JsonPropertyName("state_sha256")]
            public string? StateSha256 { get; set; } = null;
        }

        #endregion
    }
}
