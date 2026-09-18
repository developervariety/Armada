namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.TypedDecisions;
    using Armada.Core.Settings;
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
                            recorder, store, () => settings.TypedDecisions, new LoggingModule());

                        string report = await CallAsync(handlers, McpTypedDecisionDataTools.LabelsToolName, new { }).ConfigureAwait(false);
                        AssertContains("custom:house_rule", report, "the labels report names the opted-in custom decision");
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
                        AssertTrue(atMinimum[0].Trainable, "three samples reaches the minimum");
                        AssertTrue(atMinimum[0].NotTrainableReason == null, "a trainable decision has no reason");
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
                            recorder, store, () => settings.TypedDecisions, new LoggingModule());

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
