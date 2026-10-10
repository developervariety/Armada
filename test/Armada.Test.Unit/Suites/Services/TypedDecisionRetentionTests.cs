namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.TypedDecisions;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using Armada.Server.Mcp.Tools;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Phase 0 of the local-classifier programme: the host-local store of REDACTED decision state,
    /// the operator reversal that labels a gated outcome wrong, and the per-decision report of what
    /// has been retained. The rules under test are the owner's: retention is opt-in per decision,
    /// the retained text never enters an event payload, and a decision short of the minimum sample
    /// count is reported as not trainable rather than silently skipped.
    /// </summary>
    public sealed class TypedDecisionRetentionTests : TestSuite
    {
        /// <summary>Suite name.</summary>
        public override string Name => "Typed Decision Retention";

        /// <summary>Run all tests.</summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("Retention is off until the feature AND the decision opt in", () =>
            {
                ArmadaSettings settings = new ArmadaSettings();
                settings.TypedDecisions.Decisions["failure_cause"] = new TypedDecisionRuleSettings();

                AssertFalse(TypedDecisionSampleStore.Retains(settings.TypedDecisions, "failure_cause"),
                    "retention is off by default");

                settings.TypedDecisions.Retention.Enabled = true;
                AssertFalse(TypedDecisionSampleStore.Retains(settings.TypedDecisions, "failure_cause"),
                    "enabling the feature alone must retain nothing");

                settings.TypedDecisions.Decisions["failure_cause"].RetainState = true;
                AssertTrue(TypedDecisionSampleStore.Retains(settings.TypedDecisions, "failure_cause"),
                    "the decision's own opt-in turns retention on");

                settings.TypedDecisions.Retention.Enabled = false;
                AssertFalse(TypedDecisionSampleStore.Retains(settings.TypedDecisions, "failure_cause"),
                    "the feature switch is the kill switch for every decision");
                AssertFalse(TypedDecisionSampleStore.Retains(settings.TypedDecisions, "unknown_decision"),
                    "an unknown decision retains nothing");
                return Task.CompletedTask;
            });

            await RunTest("A custom decision opts in with its own retainState and the report lists it", async () =>
            {
                ArmadaSettings settings = RetainingSettings("failure_cause");
                settings.TypedDecisions.Custom["house_rule"] = new CustomTypedDecisionSettings();

                AssertFalse(TypedDecisionSampleStore.Retains(settings.TypedDecisions, "custom:house_rule"),
                    "a custom decision that has not opted in retains nothing");
                settings.TypedDecisions.Custom["house_rule"].RetainState = true;
                AssertTrue(TypedDecisionSampleStore.Retains(settings.TypedDecisions, "custom:house_rule"),
                    "its own retainState turns retention on");
                AssertFalse(TypedDecisionSampleStore.Retains(settings.TypedDecisions, "custom:missing"),
                    "an unknown custom decision retains nothing");
                AssertFalse(TypedDecisionSampleStore.Retains(settings.TypedDecisions, "captain_tool"),
                    "the general captain tool has no opt-in and retains nothing");

                List<string> optedIn = TypedDecisionSampleStore.OptedInDecisionPoints(settings.TypedDecisions);
                AssertTrue(optedIn.Contains("custom:house_rule"), "the opted-in list names the custom decision");
                AssertTrue(optedIn.Contains("failure_cause"), "and the built-in one");
                foreach (string decisionPoint in optedIn)
                    AssertTrue(TypedDecisionSampleStore.Retains(settings.TypedDecisions, decisionPoint),
                        "every listed decision point is one Retains answers yes for: " + decisionPoint);

                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string dataDirectory = NewTempDir("custom-report");
                    try
                    {
                        TypedDecisionSampleStore store = new TypedDecisionSampleStore(dataDirectory, new LoggingModule());
                        TypedDecisionRecorder recorder = new TypedDecisionRecorder(
                            testDb.Driver, new LoggingModule(), store, () => settings.TypedDecisions);
                        Dictionary<string, Func<JsonElement?, Task<object>>> handlers =
                            new Dictionary<string, Func<JsonElement?, Task<object>>>();
                        McpTypedDecisionDataTools.Register(
                            (name, description, schema, handler) => handlers[name] = handler,
                            testDb.Driver, recorder, store, () => settings.TypedDecisions, new LoggingModule());

                        string report = await CallAsync(handlers, McpTypedDecisionDataTools.LabelsToolName, new { }).ConfigureAwait(false);
                        AssertContains("custom:house_rule", report, "the labels report names the opted-in custom decision");
                    }
                    finally { SafeDelete(dataDirectory); }
                }
            });

            await RunTest("Retention preserves request provenance and distributions without event question text", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string dataDirectory = NewTempDir("provenance");
                    try
                    {
                        ArmadaSettings settings = RetainingSettings("failure_cause");
                        TypedDecisionSampleStore store = new TypedDecisionSampleStore(dataDirectory, new LoggingModule());
                        TypedDecisionRecorder recorder = new TypedDecisionRecorder(testDb.Driver, new LoggingModule(), store, () => settings.TypedDecisions);
                        TypedDecisionEventContext context = new TypedDecisionEventContext
                        {
                            DecisionPoint = "failure_cause", RuleVerdict = "Infra", RedactedState = "fixture",
                            Result = new TypedDecisionResult
                            {
                                Available = true, Model = "jev-1.13.0", BatchSize = 2,
                                Provenance = new TypedDecisionProvenance
                                {
                                    QuestionsJson = "{\"q\":{\"instructions\":\"QUESTION-SENTINEL\"}}",
                                    QuestionsSha256 = "redacted-hash", WireQuestionsSha256 = "wire-hash",
                                    RequestSha256 = "request-hash", BatchItemIndex = 1
                                },
                                Answers = new Dictionary<string, TypedAnswer>
                                {
                                    ["cause"] = new TypedAnswer
                                    {
                                        Type = "choice", Choice = "provider", Confidence = 0.6,
                                        Probabilities = new Dictionary<string, double> { ["provider"] = 0.8, ["unclear"] = 0.2 }
                                    }
                                }
                            }
                        };
                        ArmadaEvent? evt = await recorder.RecordGatedAsync(context, default).ConfigureAwait(false);
                        TypedDecisionSample sample = ReadSamples(store, "failure_cause").Single();
                        AssertEqual("jev-1.13.0", sample.Model);
                        AssertEqual(2, sample.BatchSize);
                        AssertEqual(1, sample.Provenance!.BatchItemIndex);
                        AssertContains("QUESTION-SENTINEL", sample.Provenance.QuestionsJson);
                        AssertEqual(0.8, sample.Answers!["cause"].Probabilities!["provider"]);
                        AssertEqual(0.6, sample.Answers["cause"].Confidence);
                        AssertContains("wire-hash", evt!.Payload!);
                        AssertContains("request-hash", evt.Payload!);
                        AssertFalse(evt.Payload!.Contains("QUESTION-SENTINEL", StringComparison.Ordinal));
                        TypedDecisionEventContext failedEvidence = new TypedDecisionEventContext
                        {
                            DecisionPoint = "failure_cause", RuleVerdict = "Infra", RedactedState = "fixture",
                            Result = new TypedDecisionResult
                            {
                                Available = true, Model = "jev-1.13.0",
                                Provenance = TypeSafeDecisionClient.CaptureProvenance("{", "{}"),
                                Answers = context.Result.Answers
                            }
                        };
                        ArmadaEvent? diagnosticEvent = await recorder.RecordGatedAsync(failedEvidence, default).ConfigureAwait(false);
                        AssertContains("capture_failed", diagnosticEvent!.Payload!);
                        AssertEqual("capture_failed", ReadSamples(store, "failure_cause")[1].Provenance!.UnavailableReason);
                        AssertTrue(failedEvidence.Result.Available, "evidence failure must not replace a usable answer");
                    }
                    finally { SafeDelete(dataDirectory); }
                }
            });

            await RunTest("A retained call keeps the redacted state in the store and out of the event", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string dataDirectory = NewTempDir("retain");
                    try
                    {
                        ArmadaSettings settings = RetainingSettings("failure_cause");
                        TypedDecisionSampleStore store = new TypedDecisionSampleStore(dataDirectory, new LoggingModule());
                        TypedDecisionRecorder recorder = new TypedDecisionRecorder(
                            testDb.Driver, new LoggingModule(), store, () => settings.TypedDecisions);

                        const string state = "the captain reported PROVIDER-SENTINEL in its log tail";
                        ArmadaEvent? evt = await recorder.RecordGatedAsync(Context("failure_cause", state), default).ConfigureAwait(false);

                        AssertTrue(evt != null, "the decision event is written");
                        AssertFalse((evt!.Payload ?? "").Contains("PROVIDER-SENTINEL", StringComparison.Ordinal),
                            "the event must never carry the state");
                        AssertContains("state_sha256", evt.Payload ?? "", "the event carries the hash");

                        List<TypedDecisionSample> samples = ReadSamples(store, "failure_cause");
                        AssertEqual(1, samples.Count, "one sample per retained call");
                        AssertEqual(state, samples[0].RedactedState, "the sample carries the redacted state");
                        AssertEqual(evt.Id, samples[0].EventId, "the sample names its event");
                        AssertEqual("Infra", samples[0].RuleVerdict, "the rule verdict is kept as the label to beat");
                        AssertTrue(!String.IsNullOrEmpty(samples[0].StateSha256), "the sample carries the state hash");
                    }
                    finally { SafeDelete(dataDirectory); }
                }
            });

            await RunTest("A decision that has not opted in retains nothing", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string dataDirectory = NewTempDir("retain-off");
                    try
                    {
                        ArmadaSettings settings = RetainingSettings("failure_cause");
                        settings.TypedDecisions.Decisions["review_substance"] = new TypedDecisionRuleSettings();
                        TypedDecisionSampleStore store = new TypedDecisionSampleStore(dataDirectory, new LoggingModule());
                        TypedDecisionRecorder recorder = new TypedDecisionRecorder(
                            testDb.Driver, new LoggingModule(), store, () => settings.TypedDecisions);

                        await recorder.RecordGatedAsync(Context("review_substance", "a Judge narrative"), default).ConfigureAwait(false);

                        AssertEqual(0, ReadSamples(store, "review_substance").Count, "an opted-out decision retains nothing");
                    }
                    finally { SafeDelete(dataDirectory); }
                }
            });

            await RunTest("An operator reversal is recorded against its decision event and labels the sample", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string dataDirectory = NewTempDir("reversal");
                    try
                    {
                        ArmadaSettings settings = RetainingSettings("failure_cause");
                        TypedDecisionSampleStore store = new TypedDecisionSampleStore(dataDirectory, new LoggingModule());
                        TypedDecisionRecorder recorder = new TypedDecisionRecorder(
                            testDb.Driver, new LoggingModule(), store, () => settings.TypedDecisions);

                        ArmadaEvent? gated = await recorder.RecordGatedAsync(
                            Context("failure_cause", "a build log tail"), default).ConfigureAwait(false);

                        TypedDecisionReversalResult result = await recorder.RecordReversalAsync(
                            gated!.Id, "TestFail", "the run named its failed tests", "operator-1").ConfigureAwait(false);

                        AssertTrue(result.Success, "the reversal is recorded");
                        AssertEqual("failure_cause", result.DecisionPoint, "the decision comes from the reversed event");
                        AssertTrue(result.Labelled, "a retained decision also gets a labelled sample");

                        List<ArmadaEvent> reversals = await testDb.Driver.Events
                            .EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeReversed).ConfigureAwait(false);
                        AssertEqual(1, reversals.Count, "exactly one reversal event");
                        AssertContains(gated.Id, reversals[0].Payload ?? "", "the reversal names the original event");
                        AssertContains("TestFail", reversals[0].Payload ?? "", "the reversal carries the corrected verdict");

                        List<TypedDecisionSample> samples = ReadSamples(store, "failure_cause");
                        TypedDecisionSample? label = samples.FirstOrDefault(x => x.Kind == TypedDecisionSampleStore.KindReversal);
                        AssertTrue(label != null, "the store holds the reversal label");
                        AssertEqual("TestFail", label!.CorrectedVerdict, "the label carries the correct answer");
                        AssertEqual(samples[0].StateSha256, label.StateSha256, "the label joins to its call by state hash");
                    }
                    finally { SafeDelete(dataDirectory); }
                }
            });

            await RunTest("A reversal is refused for an unknown or non-decision event", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    TypedDecisionRecorder recorder = new TypedDecisionRecorder(testDb.Driver, new LoggingModule());

                    TypedDecisionReversalResult missing = await recorder
                        .RecordReversalAsync("evt_does_not_exist", "TestFail", "why", "operator-1").ConfigureAwait(false);
                    AssertFalse(missing.Success, "an unknown event is refused");
                    AssertTrue(!String.IsNullOrEmpty(missing.Refusal), "the refusal says why");

                    ArmadaEvent other = await testDb.Driver.Events
                        .CreateAsync(new ArmadaEvent("mission.completed", "not a decision")).ConfigureAwait(false);
                    TypedDecisionReversalResult wrongKind = await recorder
                        .RecordReversalAsync(other.Id, "TestFail", "why", "operator-1").ConfigureAwait(false);
                    AssertFalse(wrongKind.Success, "a non-decision event is refused");

                    TypedDecisionReversalResult noVerdict = await recorder
                        .RecordReversalAsync(other.Id, "  ", "why", "operator-1").ConfigureAwait(false);
                    AssertFalse(noVerdict.Success, "a reversal without a corrected verdict is refused");
                }
            });

            await RunTest("The report names every decision and says which are not trainable yet", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string dataDirectory = NewTempDir("report");
                    try
                    {
                        ArmadaSettings settings = RetainingSettings("failure_cause");
                        settings.TypedDecisions.Retention.MinimumSamplesPerDecision = 3;
                        TypedDecisionSampleStore store = new TypedDecisionSampleStore(dataDirectory, new LoggingModule());
                        TypedDecisionRecorder recorder = new TypedDecisionRecorder(
                            testDb.Driver, new LoggingModule(), store, () => settings.TypedDecisions);

                        for (int i = 0; i < 2; i++)
                            await recorder.RecordGatedAsync(Context("failure_cause", "state " + i), default).ConfigureAwait(false);

                        List<TypedDecisionSampleCount> shortOfMinimum = store.Summarize(3);
                        AssertEqual(1, shortOfMinimum.Count, "the decision with samples is reported");
                        AssertEqual(2, shortOfMinimum[0].Samples, "both calls are counted");
                        AssertFalse(shortOfMinimum[0].Trainable, "two samples is short of three");
                        AssertTrue(!String.IsNullOrEmpty(shortOfMinimum[0].NotTrainableReason),
                            "a decision short of the minimum says why, instead of being skipped");

                        await recorder.RecordGatedAsync(Context("failure_cause", "state 3"), default).ConfigureAwait(false);
                        List<TypedDecisionSampleCount> atMinimum = store.Summarize(3);
                        AssertTrue(atMinimum[0].MinimumSampleCountMet, "three samples reaches the raw count minimum");
                        AssertFalse(atMinimum[0].Trainable, "teacher samples alone cannot establish training readiness");
                        AssertContains("independent labels", atMinimum[0].NotTrainableReason ?? "");
                    }
                    finally { SafeDelete(dataDirectory); }
                }
            });

            await RunTest("The operator tools record a reversal and report what is retained", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string dataDirectory = NewTempDir("tools");
                    try
                    {
                        ArmadaSettings settings = RetainingSettings("failure_cause");
                        settings.TypedDecisions.Retention.MinimumSamplesPerDecision = 5;
                        TypedDecisionSampleStore store = new TypedDecisionSampleStore(dataDirectory, new LoggingModule());
                        TypedDecisionRecorder recorder = new TypedDecisionRecorder(
                            testDb.Driver, new LoggingModule(), store, () => settings.TypedDecisions);
                        Dictionary<string, Func<JsonElement?, Task<object>>> handlers =
                            new Dictionary<string, Func<JsonElement?, Task<object>>>();
                        McpTypedDecisionDataTools.Register(
                            (name, description, schema, handler) => handlers[name] = handler,
                            testDb.Driver, recorder, store, () => settings.TypedDecisions, new LoggingModule());

                        AssertTrue(handlers.ContainsKey(McpTypedDecisionDataTools.ReversalToolName), "the reversal tool registers");
                        AssertTrue(handlers.ContainsKey(McpTypedDecisionDataTools.LabelsToolName), "the report tool registers");

                        ArmadaEvent? gated = await recorder.RecordGatedAsync(
                            Context("failure_cause", "a captain output tail"), default).ConfigureAwait(false);

                        string refused = await CallAsync(handlers, McpTypedDecisionDataTools.ReversalToolName,
                            new { eventId = "evt_missing", correctedVerdict = "TestFail" }).ConfigureAwait(false);
                        AssertContains("\"Success\":false", refused, "an unknown event is refused");

                        string recorded = await CallAsync(handlers, McpTypedDecisionDataTools.ReversalToolName,
                            new { eventId = gated!.Id, correctedVerdict = "TestFail", reason = "the run named its failed tests" }).ConfigureAwait(false);
                        AssertContains("\"Success\":true", recorded, "a real decision event is reversed");
                        AssertContains("\"Labelled\":true", recorded, "the reversal is kept as a labelled example");

                        string report = await CallAsync(handlers, McpTypedDecisionDataTools.LabelsToolName, new { }).ConfigureAwait(false);
                        AssertContains("failure_cause", report, "the report names the decision");
                        AssertContains("\"Trainable\":false", report, "one sample is short of the minimum");
                        AssertContains("\"NotTrainableReason\"", report, "and the report says why");

                        settings.TypedDecisions.Retention.Enabled = false;
                        string off = await CallAsync(handlers, McpTypedDecisionDataTools.LabelsToolName, new { }).ConfigureAwait(false);
                        AssertContains("\"RetentionEnabled\":false", off, "the report states plainly that retention is off");
                    }
                    finally { SafeDelete(dataDirectory); }
                }
            });

            await RunTest("The operator retrieves one retained preflight sample in bounded pages", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string dataDirectory = NewTempDir("preflight-retrieval");
                    try
                    {
                        const string toolName = "armada_typed_decision_sample";
                        const string decisionPoint = PreflightTextAdapter.DecisionPoint;
                        const string objectiveId = "obj_synthetic";
                        ArmadaSettings settings = RetainingSettings(decisionPoint);
                        TypedDecisionSampleStore store = new TypedDecisionSampleStore(dataDirectory, new LoggingModule());
                        TypedDecisionRecorder recorder = new TypedDecisionRecorder(
                            testDb.Driver, new LoggingModule(), store, () => settings.TypedDecisions);
                        Dictionary<string, Func<JsonElement?, Task<object>>> handlers =
                            new Dictionary<string, Func<JsonElement?, Task<object>>>();
                        McpTypedDecisionDataTools.Register(
                            (name, description, schema, handler) => handlers[name] = handler,
                            testDb.Driver, recorder, store, () => settings.TypedDecisions, new LoggingModule());

                        AssertTrue(handlers.ContainsKey(toolName), "the event-scoped retained-sample handler registers");
                        AssertTrue(McpToolAccessPolicy.IsAllowed(McpTestCaller.Operator, toolName), "a global operator may call the tool");

                        AuthContext ordinaryUser = AuthContext.Authenticated("tenant-sample", "user-sample", false, false, "Bearer");
                        AuthContext tenantAdmin = AuthContext.Authenticated("tenant-sample", "admin-sample", false, true, "Bearer");
                        AuthContext missionAdmin = AuthContext.Authenticated(
                            Armada.Core.Constants.DefaultTenantId, Armada.Core.Constants.DefaultUserId,
                            true, true, "Bearer", "credential-sample", "Mission administrator");
                        missionAdmin.MissionId = "msn_synthetic";
                        AssertFalse(McpToolAccessPolicy.IsAllowed(ordinaryUser, toolName), "an ordinary user cannot call the operator tool");
                        AssertFalse(McpToolAccessPolicy.IsAllowed(tenantAdmin, toolName), "a tenant administrator cannot call the operator tool");
                        AssertFalse(McpToolAccessPolicy.IsAllowed(missionAdmin, toolName), "a mission token cannot call the operator tool even when its owner is admin");

                        // All fixture content is synthetic and already redacted. Its size forces several pages.
                        string state = String.Empty;
                        const string escapedSupplementaryCharacter = "\\uD83D\\uDE00";
                        for (int padding = 0; padding < 1024; padding++)
                        {
                            string candidate = DecisionStateRedactor.RedactState(new
                            {
                                question = new string('a', padding) + "😀" + new string('b', 5900 - padding)
                            }, 8192).Text;
                            if (candidate.IndexOf(escapedSupplementaryCharacter, StringComparison.Ordinal) == 1023)
                            {
                                state = candidate;
                                break;
                            }
                        }
                        AssertTrue(!String.IsNullOrEmpty(state),
                            "the producer's supplementary character has its canonical escape at the first page boundary");
                        AssertEqual(state, DecisionStateRedactor.RedactState(JsonNode.Parse(state), 8192).Text,
                            "the retained fixture passes current redaction unchanged");
                        TypedDecisionEventContext context = new TypedDecisionEventContext
                        {
                            DecisionPoint = decisionPoint,
                            ObjectiveId = objectiveId,
                            RuleVerdict = "Infra",
                            ModelVerdict = "TestFail",
                            Confidence = 0.93,
                            RedactedState = state,
                            Result = new TypedDecisionResult { Available = true, Answers = new Dictionary<string, TypedAnswer>() }
                        };
                        ArmadaEvent? decisionEvent = await recorder.RecordGatedAsync(context, default).ConfigureAwait(false);
                        AssertNotNull(decisionEvent, "the synthetic decision event is recorded");
                        TypedDecisionSample expected = ReadSamples(store, decisionPoint)
                            .Single(sample => sample.EventId == decisionEvent!.Id && sample.Kind == TypedDecisionSampleStore.KindDecision);
                        AssertEqual(DecisionStateRedactor.Version, expected.RedactorVersion, "the fixture uses the current redactor cohort");
                        string sampleFile = Directory.EnumerateFiles(Path.Combine(store.RootPath, decisionPoint), "*.jsonl").Single();
                        string[] validSampleLines = File.ReadAllLines(sampleFile);
                        int targetSampleLine = Array.FindIndex(validSampleLines,
                            line => line.Contains(decisionEvent!.Id, StringComparison.Ordinal));
                        AssertTrue(targetSampleLine >= 0, "the event has a retained sample line");

                        if (targetSampleLine >= 0)
                        {
                            string originalSampleLine = validSampleLines[targetSampleLine];
                            JsonObject alteredHash = JsonNode.Parse(validSampleLines[targetSampleLine])!.AsObject();
                            alteredHash["state_sha256"] = new string('0', 64);
                            validSampleLines[targetSampleLine] = alteredHash.ToJsonString();
                            File.WriteAllLines(sampleFile, validSampleLines);
                            string hashMismatch = await CallAsAsync(handlers, toolName,
                                new { eventId = decisionEvent!.Id, offset = 0, maxChars = 32 }, McpTestCaller.Operator).ConfigureAwait(false);
                            AssertEqual("unavailable", ParseRetainedSamplePage(hashMismatch)?.Availability,
                                "a sample whose stored digest differs from the event is refused");

                            JsonObject alteredVersion = JsonNode.Parse(validSampleLines[targetSampleLine])!.AsObject();
                            alteredVersion["state_sha256"] = expected.StateSha256;
                            alteredVersion["redactor_version"] = DecisionStateRedactor.Version - 1;
                            validSampleLines[targetSampleLine] = alteredVersion.ToJsonString();
                            File.WriteAllLines(sampleFile, validSampleLines);
                            string staleRedactor = await CallAsAsync(handlers, toolName,
                                new { eventId = decisionEvent!.Id, offset = 0, maxChars = 32 }, McpTestCaller.Operator).ConfigureAwait(false);
                            AssertEqual("unavailable", ParseRetainedSamplePage(staleRedactor)?.Availability,
                                "a sample from an older redactor version is refused");

                            validSampleLines[targetSampleLine] = originalSampleLine;
                            File.WriteAllLines(sampleFile, validSampleLines);
                        }

                        if (!handlers.ContainsKey(toolName)) return;
                        string firstPage = await CallAsAsync(handlers, toolName,
                            new { eventId = decisionEvent!.Id, offset = 0, maxChars = 999999 }, McpTestCaller.Operator).ConfigureAwait(false);
                        AssertContains("\"Success\":true", firstPage, "the operator retrieves the matching event sample");
                        AssertContains(decisionEvent.Id, firstPage, "the response is bound to the requested event");
                        AssertContains(decisionPoint, firstPage, "the response names the decision point");
                        AssertContains(expected.StateSha256, firstPage, "the response provides the retained-state digest");
                        AssertContains("not_recorded", firstPage, "the specific Q4 rationale is stated as not recorded");
                        AssertContains("\"Q4SpecificPremise\":null", firstPage, "the tool does not invent a Q4 premise");
                        AssertFalse(firstPage.Contains("\"Provenance\"", StringComparison.Ordinal), "the response omits retained request provenance");
                        AssertFalse(firstPage.Contains("\"Model\"", StringComparison.Ordinal), "the response omits provider metadata");
                        AssertFalse(firstPage.Contains("\"Answers\"", StringComparison.Ordinal), "the response omits unrelated answer data");
                        AssertFalse(firstPage.Contains(objectiveId, StringComparison.Ordinal), "the response omits objective ownership metadata");

                        RetainedSamplePage? oversizedPage = ParseRetainedSamplePage(firstPage);
                        AssertNotNull(oversizedPage, "the tool returns its typed page response");
                        AssertTrue(oversizedPage!.Success, "the registered handler reports success to the operator");
                        AssertEqual(decisionEvent!.Id, oversizedPage.EventId, "the typed response is bound to the requested event");
                        AssertEqual(decisionPoint, oversizedPage.DecisionPoint, "the typed response names the decision point");
                        AssertEqual(expected.StateSha256, oversizedPage.StateSha256, "the typed response carries the stored state digest");
                        AssertEqual(DecisionStateRedactor.Version, oversizedPage.RedactorVersion,
                            "the typed response identifies the current redactor version");
                        AssertEqual("not_recorded", oversizedPage.Q4RationaleStatus,
                            "the typed response marks the specific rationale as not recorded");
                        AssertNull(oversizedPage.Q4SpecificPremise, "the typed response does not invent a Q4 premise");
                        AssertTrue((oversizedPage!.PageText ?? String.Empty).Length <= 4096,
                            "the server caps a requested oversized page");
                        AssertEqual(expected.RedactedState.Length, oversizedPage.TotalChars,
                            "the total length describes only the retained redacted state");
                        AssertTrue(oversizedPage.NextOffset > 0 && oversizedPage.NextOffset < oversizedPage.TotalChars,
                            "the first page is bounded and incomplete");

                        StringBuilder assembled = new StringBuilder();
                        int offset = 0;
                        while (offset < expected.RedactedState.Length)
                        {
                            string pageJson = await CallAsAsync(handlers, toolName,
                                new { eventId = decisionEvent!.Id, offset, maxChars = 1024 }, McpTestCaller.Operator).ConfigureAwait(false);
                            AssertContains("\"Success\":true", pageJson, "each page returns successfully");
                            RetainedSamplePage? page = ParseRetainedSamplePage(pageJson);
                            AssertNotNull(page, "each page uses the typed response shape");
                            string text = page!.PageText ?? String.Empty;
                            AssertTrue(text.Length <= 1024, "each requested page stays within its cap");
                            AssertEqual(offset, page.Offset, "each page reports its requested offset");
                            assembled.Append(text);
                            int nextOffset = page.NextOffset;
                            AssertTrue(nextOffset > offset, "pagination advances beyond the previous offset");
                            offset = nextOffset;
                        }
                        AssertEqual(expected.RedactedState, assembled.ToString(), "the pages reproduce exactly the retained redacted state");
                        string assembledDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(assembled.ToString()))).ToLowerInvariant();
                        AssertEqual(expected.StateSha256, assembledDigest,
                            "pages preserve the producer's canonical supplementary-character escape and exact UTF-8 digest");
                        AssertEqual(expected.StateBytes, Encoding.UTF8.GetByteCount(assembled.ToString()),
                            "bounded pages preserve the exact UTF-8 byte count");

                        string missing = await CallAsAsync(handlers, toolName,
                            new { eventId = "evt_missing", offset = 0, maxChars = 32 }, McpTestCaller.Operator).ConfigureAwait(false);
                        AssertContains("\"Success\":false", missing, "an unknown event does not return any sample");
                        RetainedSamplePage? missingEvent = ParseRetainedSamplePage(missing);
                        AssertEqual("event_not_found", missingEvent?.Availability, "an absent event is distinct from an unretained event");

                        settings.TypedDecisions.Retention.Enabled = false;
                        ArmadaEvent? unretainedEvent = await recorder.RecordGatedAsync(
                            Context(decisionPoint, "{\"question\":\"synthetic unretained event\"}"), default).ConfigureAwait(false);
                        settings.TypedDecisions.Retention.Enabled = true;
                        string unretained = await CallAsAsync(handlers, toolName,
                            new { eventId = unretainedEvent!.Id, offset = 0, maxChars = 32 }, McpTestCaller.Operator).ConfigureAwait(false);
                        AssertEqual("sample_not_retained", ParseRetainedSamplePage(unretained)?.Availability,
                            "a typed preflight event without an opted-in sample is explicit");

                        string wrongDecisionState = DecisionStateRedactor.RedactState(new { question = "synthetic wrong decision point" }, 1024).Text;
                        ArmadaEvent? wrongDecisionEvent = await recorder.RecordGatedAsync(
                            Context("failure_cause", wrongDecisionState), default).ConfigureAwait(false);
                        string wrongDecision = await CallAsAsync(handlers, toolName,
                            new { eventId = wrongDecisionEvent!.Id, offset = 0, maxChars = 32 }, McpTestCaller.Operator).ConfigureAwait(false);
                        AssertEqual("wrong_decision_point", ParseRetainedSamplePage(wrongDecision)?.Availability,
                            "a different typed decision cannot be read through the preflight tool");

                        string unsafeState = "{\"api_key\":\"synthetic private marker\"}";
                        ArmadaEvent? unsafeEvent = await recorder.RecordGatedAsync(
                            Context(decisionPoint, unsafeState), default).ConfigureAwait(false);
                        string unsafeResponse = await CallAsAsync(handlers, toolName,
                            new { eventId = unsafeEvent!.Id, offset = 0, maxChars = 32 }, McpTestCaller.Operator).ConfigureAwait(false);
                        AssertEqual("unavailable", ParseRetainedSamplePage(unsafeResponse)?.Availability,
                            "a stored state that changes under current redaction is refused");
                        AssertFalse(unsafeResponse.Contains("synthetic private marker", StringComparison.Ordinal),
                            "the changed state is never returned");

                        string tenantDenied = await CallAsAsync(handlers, toolName,
                            new { eventId = decisionEvent.Id, offset = 0, maxChars = 32 }, tenantAdmin).ConfigureAwait(false);
                        AssertEqual("forbidden", ParseRetainedSamplePage(tenantDenied)?.Availability,
                            "direct handler calls also refuse a tenant administrator");

                        string denied = await CallAsAsync(handlers, toolName,
                            new { eventId = decisionEvent.Id, offset = 0, maxChars = 32 }, missionAdmin).ConfigureAwait(false);
                        AssertContains("\"Success\":false", denied, "the handler enforces global-admin scope even when called directly");

                        byte[] originalSampleBytes = File.ReadAllBytes(sampleFile);
                        byte[] overCapSampleBytes = new byte[(8 * 1024 * 1024) + 1];
                        Array.Fill(overCapSampleBytes, (byte)'x');
                        try
                        {
                            File.WriteAllBytes(sampleFile, overCapSampleBytes);
                            string overCap = await CallAsAsync(handlers, toolName,
                                new { eventId = decisionEvent.Id, offset = 0, maxChars = 32 }, McpTestCaller.Operator).ConfigureAwait(false);
                            RetainedSamplePage? overCapPage = ParseRetainedSamplePage(overCap);
                            AssertEqual("scan_incomplete", overCapPage?.Availability,
                                "a daily file above the fixed 8 MiB byte cap is refused");
                            AssertNull(overCapPage?.PageText, "an over-cap file returns no retained text");
                        }
                        finally
                        {
                            File.WriteAllBytes(sampleFile, originalSampleBytes);
                        }

                        File.AppendAllText(sampleFile, "{corrupt synthetic line}\n");
                        string incomplete = await CallAsAsync(handlers, toolName,
                            new { eventId = decisionEvent.Id, offset = 0, maxChars = 32 }, McpTestCaller.Operator).ConfigureAwait(false);
                        RetainedSamplePage? incompletePage = ParseRetainedSamplePage(incomplete);
                        AssertEqual("scan_incomplete", incompletePage?.Availability,
                            "a corrupt daily file does not read as an absent sample");
                        AssertNull(incompletePage?.PageText, "an incomplete scan returns no retained text");
                    }
                    finally { SafeDelete(dataDirectory); }
                }
            });

            await RunTest("A sample is stamped with the redactor version and only the current cohort counts towards training", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string dataDirectory = NewTempDir("cohort");
                    try
                    {
                        ArmadaSettings settings = RetainingSettings("failure_cause");
                        TypedDecisionSampleStore store = new TypedDecisionSampleStore(dataDirectory, new LoggingModule());
                        TypedDecisionRecorder recorder = new TypedDecisionRecorder(
                            testDb.Driver, new LoggingModule(), store, () => settings.TypedDecisions);

                        await recorder.RecordGatedAsync(Context("failure_cause", "current rules 1"), default).ConfigureAwait(false);
                        await recorder.RecordGatedAsync(Context("failure_cause", "current rules 2"), default).ConfigureAwait(false);

                        List<TypedDecisionSample> samples = ReadSamples(store, "failure_cause");
                        AssertEqual(DecisionStateRedactor.Version, samples[0].RedactorVersion,
                            "a retained sample names the redaction rules that produced it");

                        // Lines written under older rules, and one written before stamping existed.
                        string folder = Path.Combine(store.RootPath, "failure_cause");
                        string olderFile = Path.Combine(folder, "2026-01-01.jsonl");
                        File.WriteAllText(olderFile,
                            "{\"kind\":\"decision\",\"decision_point\":\"failure_cause\",\"redactor_version\":" + (DecisionStateRedactor.Version - 1) + "}\n"
                            + "{\"kind\":\"decision\",\"decision_point\":\"failure_cause\"}\n");

                        List<TypedDecisionSampleCount> counts = store.Summarize(3);
                        TypedDecisionSampleCount count = counts.Single(x => x.DecisionPoint == "failure_cause");
                        AssertEqual(4, count.Samples, "every retained call is counted");
                        AssertEqual(2, count.CurrentSamples, "only the two current-rule samples are in the training cohort");
                        AssertEqual(DecisionStateRedactor.Version, count.CurrentRedactorVersion, "the report names the running rules");
                        AssertEqual(1, count.SamplesByRedactorVersion[DecisionStateRedactor.Version - 1], "the older cohort is reported, not hidden");
                        AssertEqual(1, count.SamplesByRedactorVersion[0], "an unstamped line is its own unknown cohort");
                        AssertFalse(count.Trainable, "four samples in total, but only two under the running rules, is short of three");
                        AssertContains("do not count", count.NotTrainableReason ?? "", "the reason says the older samples were left out");
                    }
                    finally { SafeDelete(dataDirectory); }
                }
            });

            await RunTest("Sample files are plain UTF-8 JSON lines with no byte-order mark", async () =>
            {
                using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                {
                    string dataDirectory = NewTempDir("nobom");
                    try
                    {
                        ArmadaSettings settings = RetainingSettings("failure_cause");
                        TypedDecisionSampleStore store = new TypedDecisionSampleStore(dataDirectory, new LoggingModule());
                        TypedDecisionRecorder recorder = new TypedDecisionRecorder(
                            testDb.Driver, new LoggingModule(), store, () => settings.TypedDecisions);
                        await recorder.RecordGatedAsync(Context("failure_cause", "first line of the day"), default).ConfigureAwait(false);

                        string file = Directory.EnumerateFiles(Path.Combine(store.RootPath, "failure_cause"), "*.jsonl").Single();
                        byte[] bytes = File.ReadAllBytes(file);
                        AssertEqual((byte)'{', bytes[0], "the first byte of a sample file is the opening brace, not a byte-order mark");
                        using (JsonDocument document = JsonDocument.Parse(File.ReadAllLines(file)[0]))
                        {
                            AssertTrue(document.RootElement.ValueKind == JsonValueKind.Object, "the first line parses as strict JSON");
                        }
                    }
                    finally { SafeDelete(dataDirectory); }
                }
            });

            await RunTest("Prune deletes only sample files older than the retention window", () =>
            {
                string dataDirectory = NewTempDir("prune");
                try
                {
                    TypedDecisionSampleStore store = new TypedDecisionSampleStore(dataDirectory, new LoggingModule());
                    string folder = Path.Combine(store.RootPath, "failure_cause");
                    Directory.CreateDirectory(folder);
                    DateTime now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
                    string old = Path.Combine(folder, now.AddDays(-40).ToString("yyyy-MM-dd") + ".jsonl");
                    string recent = Path.Combine(folder, now.AddDays(-2).ToString("yyyy-MM-dd") + ".jsonl");
                    File.WriteAllText(old, "{}\n");
                    File.WriteAllText(recent, "{}\n");

                    AssertEqual(0, store.Prune(0, now), "a window of zero deletes nothing");
                    AssertEqual(1, store.Prune(30, now), "only the file outside the window is deleted");
                    AssertFalse(File.Exists(old), "the old file is gone");
                    AssertTrue(File.Exists(recent), "the recent file is kept");
                    return Task.CompletedTask;
                }
                finally { SafeDelete(dataDirectory); }
            });
        }

        #region Private-Methods

        private static ArmadaSettings RetainingSettings(string decisionPoint)
        {
            ArmadaSettings settings = new ArmadaSettings();
            settings.TypedDecisions.Retention.Enabled = true;
            settings.TypedDecisions.Decisions[decisionPoint] = new TypedDecisionRuleSettings { RetainState = true };
            return settings;
        }

        private static TypedDecisionEventContext Context(string decisionPoint, string state)
        {
            return new TypedDecisionEventContext
            {
                DecisionPoint = decisionPoint,
                RuleVerdict = "Infra",
                ModelVerdict = "TestFail",
                Confidence = 0.93,
                RedactedState = state,
                Result = new TypedDecisionResult { Available = true, Answers = new Dictionary<string, TypedAnswer>() }
            };
        }

        private static List<TypedDecisionSample> ReadSamples(TypedDecisionSampleStore store, string decisionPoint)
        {
            string folder = Path.Combine(store.RootPath, decisionPoint);
            List<TypedDecisionSample> samples = new List<TypedDecisionSample>();
            if (!Directory.Exists(folder)) return samples;
            JsonSerializerOptions options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            foreach (string file in Directory.EnumerateFiles(folder, "*.jsonl").OrderBy(x => x, StringComparer.Ordinal))
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    if (String.IsNullOrWhiteSpace(line)) continue;
                    TypedDecisionSample? sample = JsonSerializer.Deserialize<TypedDecisionSample>(line, options);
                    if (sample != null) samples.Add(sample);
                }
            }
            return samples;
        }

        private static async Task<string> CallAsync(
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers, string tool, object args)
        {
            using (JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(args)))
            {
                object result = await handlers[tool](document.RootElement.Clone()).ConfigureAwait(false);
                return JsonSerializer.Serialize(result);
            }
        }

        private static async Task<string> CallAsAsync(
            Dictionary<string, Func<JsonElement?, Task<object>>> handlers,
            string tool,
            object args,
            AuthContext caller)
        {
            using (JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(args)))
            {
                using (McpCallerContext.Begin(caller))
                {
                    object result = await handlers[tool](document.RootElement.Clone()).ConfigureAwait(false);
                    return JsonSerializer.Serialize(result);
                }
            }
        }

        private static RetainedSamplePage? ParseRetainedSamplePage(string json)
        {
            return JsonSerializer.Deserialize<RetainedSamplePage>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        private sealed class RetainedSamplePage
        {
            /// <summary>Whether the lookup returned a usable page.</summary>
            public bool Success { get; set; }
            /// <summary>The typed-decision event identifier.</summary>
            public string? EventId { get; set; }
            /// <summary>The retained decision point.</summary>
            public string? DecisionPoint { get; set; }
            /// <summary>SHA-256 digest of the complete redacted state.</summary>
            public string? StateSha256 { get; set; }
            /// <summary>Redactor version that produced the retained state.</summary>
            public int RedactorVersion { get; set; }
            /// <summary>Character offset of this page.</summary>
            public int Offset { get; set; }
            /// <summary>Total character count of the redacted state.</summary>
            public int TotalChars { get; set; }
            /// <summary>Character offset for the next page.</summary>
            public int NextOffset { get; set; }
            /// <summary>This page's redacted state text.</summary>
            public string? PageText { get; set; }
            /// <summary>Whether this page reaches the end of the state.</summary>
            public bool Complete { get; set; }
            /// <summary>Specific Q4 premise reference, absent when not recorded.</summary>
            public string? Q4SpecificPremise { get; set; }
            /// <summary>States whether a specific Q4 rationale was retained.</summary>
            public string? Q4RationaleStatus { get; set; }
        }

        private static string NewTempDir(string prefix)
        {
            string path = Path.Combine(Path.GetTempPath(), "armada-" + prefix + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        #endregion
    }
}
