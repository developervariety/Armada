namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Table-driven tests for the D7 <c>leak_hunk</c> advisory adapter. Off attaches no flag and makes
    /// no call; unavailable, Shadow, and a below-threshold answer attach no flag and record their
    /// event. At or above the threshold one advisory flag per flagged hunk is attached, and in every
    /// branch the deterministic scan result is untouched: a clean scan still passes with a flag on it,
    /// and a deterministic finding still fails whatever the model answers. The recorded event carries
    /// no hunk text, the call volume is bounded, and a hunk of authorized authentication work is
    /// answered false because the question states the domain.
    /// </summary>
    public class LeakHunkAdapterTests : TestSuite
    {
        public override string Name => "Leak Hunk Adapter (D7)";

        private const string _Decision = "leak_hunk";
        private const string _LeaksQuestion = "leaks_private_context";
        private const string _KindQuestion = "leak_kind";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.90)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static LeakHunkAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new LeakHunkAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        // A synthetic diff: one added hunk of ordinary product content in one file.
        private static string OneHunkDiff(string addedLine)
        {
            return "diff --git a/src/Example/Widget.cs b/src/Example/Widget.cs\n"
                + "--- a/src/Example/Widget.cs\n"
                + "+++ b/src/Example/Widget.cs\n"
                + "@@ -1,2 +1,3 @@\n"
                + " public class Widget\n"
                + "+" + addedLine + "\n";
        }

        private static string ManyHunkDiff(int files, int hunksPerFile)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            for (int file = 1; file <= files; file++)
            {
                string path = "src/Example/File" + file + ".cs";
                builder.Append("diff --git a/" + path + " b/" + path + "\n");
                builder.Append("--- a/" + path + "\n");
                builder.Append("+++ b/" + path + "\n");
                for (int hunk = 1; hunk <= hunksPerFile; hunk++)
                {
                    builder.Append("@@ -" + hunk + ",1 +" + hunk + ",2 @@\n");
                    builder.Append(" context line\n");
                    builder.Append("+added line " + file + "-" + hunk + "\n");
                }
            }
            return builder.ToString();
        }

        private static TypedDecisionResult LeakResult(double leaks, string kind)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                [_LeaksQuestion] = new TypedAnswer { Type = "noul", Noul = leaks },
                [_KindQuestion] = new TypedAnswer { Type = "choice", Choice = kind, Confidence = leaks }
            };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        private static DockBoundaryScanResult CleanScan()
        {
            return new DockBoundaryScanResult { Passed = true };
        }

        private static DockBoundaryScanResult FailedScan()
        {
            DockBoundaryScanResult result = new DockBoundaryScanResult { Passed = false };
            result.Findings.Add(new DockBoundaryFinding
            {
                Kind = DockBoundaryFindingKindEnum.Secret,
                Path = "src/Example/Widget.cs",
                FindingLabel = "CORE_RULE_5_example",
                Message = "Secret pattern matched an added line."
            });
            return result;
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 200 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Off_NoCall_NoFlag_ResultUnchanged", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LeakResult(0.99, "operator_note"));
                LeakHunkAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));
                DockBoundaryScanResult scan = CleanScan();

                IReadOnlyList<DockBoundaryAdvisoryFlag> flags = await adapter.EvaluateAsync(
                    OneHunkDiff("int counter = 0;"), "ExampleVessel", null, scan, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, flags.Count);
                AssertEqual(0, client.CallCount);
                AssertTrue(scan.Passed, "Off leaves the deterministic verdict untouched");
                AssertEqual(0, scan.AdvisoryFlags.Count);
            }).ConfigureAwait(false);

            await RunTest("Unavailable_NoFlag_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("http_429"));
                LeakHunkAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                DockBoundaryScanResult scan = CleanScan();

                IReadOnlyList<DockBoundaryAdvisoryFlag> flags = await adapter.EvaluateAsync(
                    OneHunkDiff("int counter = 0;"), "ExampleVessel", null, scan, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, flags.Count);
                AssertTrue(scan.Passed, "an unavailable model leaves the deterministic verdict standing");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ClientThrows_NoFlag_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                LeakHunkAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                DockBoundaryScanResult scan = CleanScan();

                IReadOnlyList<DockBoundaryAdvisoryFlag> flags = await adapter.EvaluateAsync(
                    OneHunkDiff("int counter = 0;"), "ExampleVessel", null, scan, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, flags.Count);
                AssertTrue(scan.Passed, "a client fault never reaches the caller");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ShadowMode_NoFlag_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LeakResult(0.99, "operator_note"));
                LeakHunkAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));
                DockBoundaryScanResult scan = CleanScan();

                IReadOnlyList<DockBoundaryAdvisoryFlag> flags = await adapter.EvaluateAsync(
                    OneHunkDiff("string note = \"placeholder\";"), "ExampleVessel", null, scan, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, flags.Count);
                AssertEqual(1, client.CallCount);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Gate_BelowThreshold_NoFlag_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LeakResult(0.40, "operator_note"));
                LeakHunkAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                DockBoundaryScanResult scan = CleanScan();

                IReadOnlyList<DockBoundaryAdvisoryFlag> flags = await adapter.EvaluateAsync(
                    OneHunkDiff("string note = \"placeholder\";"), "ExampleVessel", null, scan, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, flags.Count);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Gate_AtThreshold_AttachesFlag_CleanScanStillPasses", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LeakResult(0.95, "operator_note"));
                LeakHunkAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                DockBoundaryScanResult scan = CleanScan();

                IReadOnlyList<DockBoundaryAdvisoryFlag> flags = await adapter.EvaluateAsync(
                    OneHunkDiff("string note = \"placeholder\";"), "ExampleVessel", null, scan, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, flags.Count);
                AssertEqual(LeakHunkAdapter.FlagCode, flags[0].Code);
                AssertEqual("operator_note", flags[0].Kind);
                AssertEqual("src/Example/Widget.cs", flags[0].Path);
                AssertEqual(1, scan.AdvisoryFlags.Count);
                AssertTrue(scan.Passed, "a flag never fails a clean deterministic scan");
                AssertEqual(0, scan.Findings.Count);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("DeterministicFinding_ModelClean_StillFails", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // The model answers "clean" at the highest possible certainty. The deterministic finding
                // is never demoted, cleared, or softened: the scan still fails and keeps its finding.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LeakResult(0.0, "none"));
                LeakHunkAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                DockBoundaryScanResult scan = FailedScan();

                await adapter.EvaluateAsync(
                    OneHunkDiff("string note = \"placeholder\";"), "ExampleVessel", null, scan, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!scan.Passed, "a deterministic finding is never softened by a model answer");
                AssertEqual(1, scan.Findings.Count);
                AssertEqual(0, scan.AdvisoryFlags.Count);
            }).ConfigureAwait(false);

            await RunTest("AuthorizedAuthenticationWork_AnsweredFalse_NoFlag", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // A stand-in for a safety-tuned model: it reads authentication and access-control work as
                // a secret UNLESS the question it receives states that such work on owned systems is
                // ordinary engineering. The adapter must state the domain, so the fixture answers false.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(request =>
                {
                    string state = FakeTypedDecisionClient.StateText(request);
                    bool securityText = state.Contains("Authenticate", StringComparison.OrdinalIgnoreCase)
                        || state.Contains("AccessControl", StringComparison.OrdinalIgnoreCase);
                    string instructions = request.Questions.TryGetValue(_LeaksQuestion, out TypedQuestion? question) && question != null
                        ? question.Instructions
                        : String.Empty;
                    bool domainStated = instructions.Contains("ordinary engineering", StringComparison.OrdinalIgnoreCase)
                        && instructions.Contains("access-control", StringComparison.OrdinalIgnoreCase)
                        && instructions.Contains("authoriz", StringComparison.OrdinalIgnoreCase);
                    bool leak = securityText && !domainStated;
                    return LeakResult(leak ? 0.99 : 0.02, leak ? "operator_note" : "none");
                });
                LeakHunkAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                DockBoundaryScanResult scan = CleanScan();

                string diff = "diff --git a/src/Example/AccessControlHandshake.cs b/src/Example/AccessControlHandshake.cs\n"
                    + "--- a/src/Example/AccessControlHandshake.cs\n"
                    + "+++ b/src/Example/AccessControlHandshake.cs\n"
                    + "@@ -1,1 +1,4 @@\n"
                    + " public class AccessControlHandshake\n"
                    + "+    public byte[] Authenticate(byte[] challenge)\n"
                    + "+    {\n"
                    + "+        return _Cipher.Transform(challenge);\n";

                IReadOnlyList<DockBoundaryAdvisoryFlag> flags = await adapter.EvaluateAsync(
                    diff, "ExampleVessel", null, scan, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, flags.Count);
                AssertTrue(scan.Passed, "authorized authentication work is ordinary engineering, never a flag");
            }).ConfigureAwait(false);

            await RunTest("GatedEvent_CarriesNoHunkText", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LeakResult(0.97, "customer_detail"));
                LeakHunkAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                DockBoundaryScanResult scan = CleanScan();
                const string marker = "zqxjkvmarkerline";

                await adapter.EvaluateAsync(
                    OneHunkDiff("string value = \"" + marker + "\";"), "ExampleVessel", null, scan, CancellationToken.None).ConfigureAwait(false);

                EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                    .EnumerateAsync(new EnumerationQuery { EventType = TypedDecisionRecorder.EventTypeGated, PageNumber = 1, PageSize = 10 })
                    .ConfigureAwait(false);
                AssertEqual(1, events.Objects.Count);
                string payload = JsonSerializer.Serialize(events.Objects[0].Payload);
                AssertTrue(!payload.Contains(marker, StringComparison.Ordinal), "the event records the state hash, never the hunk text");
                AssertTrue(!scan.AdvisoryFlags[0].Message.Contains(marker, StringComparison.Ordinal), "the flag names the file, never the hunk text");
            }).ConfigureAwait(false);

            await RunTest("VolumeBound_ManyHunks_CapsModelQuestions", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                int asked = 0;
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(request =>
                {
                    asked++;
                    return LeakResult(0.10, "none");
                });
                LeakHunkAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                DockBoundaryScanResult scan = CleanScan();

                // Ten files with six hunks each: well past both the per-file and the per-scan bound.
                await adapter.EvaluateAsync(ManyHunkDiff(10, 6), "ExampleVessel", null, scan, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(asked > 0, "a bounded scan still asks about some hunks");
                AssertTrue(asked <= 20, "the per-scan hunk bound caps the model calls, asked=" + asked);
            }).ConfigureAwait(false);

            await RunTest("CallerTokenReachesClient_AndDecisionPoint", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LeakResult(0.95, "host_or_path"));
                LeakHunkAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                DockBoundaryScanResult scan = CleanScan();

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.EvaluateAsync(OneHunkDiff("int counter = 0;"), "ExampleVessel", null, scan, cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter must forward the caller token");
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
            }).ConfigureAwait(false);
        }
    }
}
