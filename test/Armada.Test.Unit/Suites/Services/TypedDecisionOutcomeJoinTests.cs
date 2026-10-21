namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.TypedDecisions;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// The typed-decision outcome join: every recorded call names the record it judged, and the join reads what
    /// later happened to that record (the mission's landing result, a linked incident, a rescue, the objective's
    /// final status), per decision and per confidence band. Calls are recorded through the real recorder, so the
    /// test also proves the recorder writes the subject the join reads; one call is written under the earlier
    /// rule-kept event name with no subject field, to prove older rows still join.
    /// </summary>
    public sealed class TypedDecisionOutcomeJoinTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Typed Decision Outcome Join";

        private sealed class Seeded
        {
            public Mission Landed { get; init; } = null!;
            public Mission Failed { get; init; } = null!;
            public Mission Active { get; init; } = null!;
            public Objective Completed { get; init; } = null!;
        }

        private static TypedDecisionResult Answered()
        {
            return new TypedDecisionResult { Available = true, Model = "test-model", InputTokens = 10, OutputTokens = 2, LatencyMs = 5 };
        }

        private static async Task<Seeded> SeedAsync(TestDatabase db)
        {
            Vessel vessel = await db.Driver.Vessels.CreateAsync(new Vessel("outcome-vessel", "https://example.test/outcome.git")).ConfigureAwait(false);
            Mission landed = await db.Driver.Missions.CreateAsync(new Mission("landed") { VesselId = vessel.Id, Status = MissionStatusEnum.Complete }).ConfigureAwait(false);
            Mission failed = await db.Driver.Missions.CreateAsync(new Mission("failed") { VesselId = vessel.Id, Status = MissionStatusEnum.Failed }).ConfigureAwait(false);
            Mission active = await db.Driver.Missions.CreateAsync(new Mission("active") { VesselId = vessel.Id, Status = MissionStatusEnum.InProgress }).ConfigureAwait(false);
            await db.Driver.Missions.CreateAsync(new Mission("rescue") { VesselId = vessel.Id, Status = MissionStatusEnum.Pending, ParentMissionId = failed.Id }).ConfigureAwait(false);
            Objective completed = await db.Driver.Objectives.CreateAsync(new Objective { Title = "done", Status = ObjectiveStatusEnum.Completed }).ConfigureAwait(false);

            AuthContext admin = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "UnitTest");
            await new IncidentService(db.Driver).CreateAsync(admin, new IncidentUpsertRequest { Title = "landed then broke", MissionId = landed.Id }).ConfigureAwait(false);

            TypedDecisionRecorder recorder = new TypedDecisionRecorder(db.Driver, new LoggingModule());
            await recorder.RecordGatedAsync(new TypedDecisionEventContext
            {
                DecisionPoint = "failure_cause", RuleVerdict = "Rescue", ModelVerdict = "environmental", Confidence = 0.95,
                Result = Answered(), MissionId = failed.Id
            }, CancellationToken.None).ConfigureAwait(false);
            await recorder.RecordRuleKeptAsync(new TypedDecisionEventContext
            {
                DecisionPoint = "failure_cause", RuleVerdict = "Rescue", ModelVerdict = "work_defect", Confidence = 0.6,
                Result = Answered(), MissionId = active.Id
            }, "below_threshold", CancellationToken.None).ConfigureAwait(false);
            await recorder.RecordRuleKeptAsync(new TypedDecisionEventContext
            {
                DecisionPoint = "preflight", RuleVerdict = "deterministic_preflight", ModelVerdict = "clear", Confidence = 0.2,
                Result = Answered(), ObjectiveId = completed.Id
            }, "below_threshold", CancellationToken.None).ConfigureAwait(false);
            await recorder.RecordRuleKeptAsync(new TypedDecisionEventContext
            {
                DecisionPoint = "papercut_merge", RuleVerdict = "distinct", Result = Answered(),
                SubjectType = TypedDecisionRecorder.SubjectTypePapercutGroup, SubjectId = "group-a,group-b"
            }, "shadow_mode", CancellationToken.None).ConfigureAwait(false);
            await recorder.RecordUnavailableAsync(new TypedDecisionEventContext
            {
                DecisionPoint = "inbox_triage", RuleVerdict = "severity_order", Result = new TypedDecisionResult { Available = false, UnavailableReason = "http_429" }
            }, CancellationToken.None).ConfigureAwait(false);

            // A call recorded under the earlier rule-kept name, before the payload carried a subject.
            await db.Driver.Events.CreateAsync(new ArmadaEvent(TypedDecisionRecorder.EventTypeRuleKeptLegacy, "decision=failure_cause rule=Rescue model=work_defect conf=0.85")
            {
                MissionId = landed.Id,
                Payload = "{\"decision\":\"failure_cause\",\"mission_id\":\"" + landed.Id + "\",\"gate_outcome\":\"below_threshold\"}"
            }).ConfigureAwait(false);

            return new Seeded { Landed = landed, Failed = failed, Active = active, Completed = completed };
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("EveryCall_IsJoinedToItsSubjectsLaterOutcome_PerDecision", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await SeedAsync(db).ConfigureAwait(false);

                    TypedDecisionOutcomeReport report = await new TypedDecisionOutcomeJoin(db.Driver).BuildAsync(DateTime.UtcNow.AddDays(-1), null).ConfigureAwait(false);

                    AssertEqual(6, report.EventsRead, "every recorded call is read, under both rule-kept names");
                    AssertFalse(report.Truncated, "the window held fewer calls than the limit");

                    TypedDecisionOutcomeRow failure = report.Decisions.Single(row => row.Name == "failure_cause");
                    AssertEqual(3, failure.Events, "three failure_cause calls");
                    AssertEqual(3, failure.WithSubject, "each names its mission, the older row through its mission id");
                    AssertEqual(2, failure.Joined, "the landed and the failed missions reached an outcome; the active one has not");
                    AssertEqual(1, failure.Landed, "one subject landed");
                    AssertEqual(1, failure.Failed, "one subject failed");
                    AssertEqual(1, failure.WithIncident, "the landed mission has a linked incident");
                    AssertEqual(1, failure.Rescued, "the failed mission was rescued");
                    AssertEqual(1, failure.ByEventType[TypedDecisionRecorder.EventTypeRuleKeptLegacy], "the older row is read under its earlier name");
                    AssertEqual(1.0, failure.SubjectShare, "every failure_cause call names a subject");

                    TypedDecisionOutcomeRow high = failure.ByConfidenceBand.Single(band => band.Name == ">=0.90");
                    AssertEqual(1, high.Events, "one call at or above 0.90");
                    AssertEqual(1, high.Failed, "that call's subject failed");
                    AssertEqual(1, high.Rescued, "and was rescued");
                    TypedDecisionOutcomeRow older = failure.ByConfidenceBand.Single(band => band.Name == "0.80-0.90");
                    AssertEqual(1, older.Landed, "the older row's confidence is read from its message line");

                    TypedDecisionOutcomeRow preflight = report.Decisions.Single(row => row.Name == "preflight");
                    AssertEqual(1, preflight.BySubjectType[TypedDecisionRecorder.SubjectTypeObjective], "preflight judges its objective");
                    AssertEqual(1, preflight.ObjectiveCompleted, "the objective completed");
                    AssertEqual(1, preflight.Joined, "a completed objective is a known outcome");

                    TypedDecisionOutcomeRow papercut = report.Decisions.Single(row => row.Name == "papercut_merge");
                    AssertEqual(1, papercut.WithSubject, "a papercut comparison names its group pair");
                    AssertEqual(0, papercut.Joined, "a papercut group has no landing outcome to join");

                    TypedDecisionOutcomeRow inbox = report.Decisions.Single(row => row.Name == "inbox_triage");
                    AssertEqual(0, inbox.WithSubject, "a call about no record names no subject");
                    AssertEqual(0.0, inbox.SubjectShare, "and its subject share says so");
                }
            }).ConfigureAwait(false);

            await RunTest("AWindowThatHoldsMoreCallsThanTheLimit_IsMarkedTruncated", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    await SeedAsync(db).ConfigureAwait(false);

                    TypedDecisionOutcomeReport report = await new TypedDecisionOutcomeJoin(db.Driver).BuildAsync(DateTime.UtcNow.AddDays(-1), null, maxEvents: 2).ConfigureAwait(false);

                    AssertEqual(2, report.EventsRead, "the read stops at the limit");
                    AssertTrue(report.Truncated, "and says the counts are a sample");
                }
            }).ConfigureAwait(false);

            await RunTest("TheOutcomeTool_ReportsTheJoin_ToAGlobalOperatorOnly", async () =>
            {
                using (TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    Seeded seeded = await SeedAsync(db).ConfigureAwait(false);
                    Dictionary<string, Func<JsonElement?, Task<object>>> handlers = new Dictionary<string, Func<JsonElement?, Task<object>>>();
                    McpTypedDecisionDataTools.Register(
                        (name, description, schema, handler) => handlers[name] = handler,
                        db.Driver, new TypedDecisionRecorder(db.Driver, new LoggingModule()), null, () => new Armada.Core.Settings.TypedDecisionSettings(), new LoggingModule());
                    AssertTrue(handlers.ContainsKey(McpTypedDecisionDataTools.OutcomesToolName), "the outcome tool registers");

                    using (JsonDocument args = JsonDocument.Parse("{\"sinceDays\":2,\"decision\":\"failure_cause\"}"))
                    {
                        AuthContext tenantAdmin = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, false, true, "UnitTest");
                        using (McpCallerContext.Begin(tenantAdmin))
                        {
                            string refused = JsonSerializer.Serialize(await handlers[McpTypedDecisionDataTools.OutcomesToolName](args.RootElement.Clone()).ConfigureAwait(false));
                            AssertContains("forbidden", refused, "a tenant administrator is refused");
                        }

                        AuthContext admin = AuthContext.Authenticated(Constants.DefaultTenantId, Constants.DefaultUserId, true, true, "UnitTest");
                        using (McpCallerContext.Begin(admin))
                        {
                            string report = JsonSerializer.Serialize(await handlers[McpTypedDecisionDataTools.OutcomesToolName](args.RootElement.Clone()).ConfigureAwait(false));
                            AssertContains("\"Success\":true", report, "the operator receives the report");
                            AssertContains("\"Name\":\"failure_cause\"", report, "the report names the requested decision");
                            AssertFalse(report.Contains("\"Name\":\"preflight\"", StringComparison.Ordinal), "and only that decision");
                            AssertContains("\"Rescued\":1", report, "the join counts the rescue");
                            AssertFalse(report.Contains(seeded.Failed.Id, StringComparison.Ordinal), "the report carries counts, not record ids");
                        }
                    }
                }
            }).ConfigureAwait(false);
        }
    }
}
