namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
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
    /// Table-driven tests for the D24 <c>lint_finding</c> adapter. Off routes nothing with no call;
    /// unavailable and shadow/below-threshold route nothing and record an event. At or above threshold,
    /// only <c>correctness</c>/<c>safety</c> findings at <c>must_fix</c> or above are marked BLOCKING and
    /// a <c>style_preference</c> finding becomes an EVIDENCE note; a consistency or below-<c>must_fix</c>
    /// finding is neither. The Linter's own result is unchanged — the verdict only re-routes. The caller
    /// token reaches the client and a client fault never reaches the caller.
    /// </summary>
    public class TypedLintFindingAdapterTests : TestSuite
    {
        public override string Name => "Typed Lint Finding Adapter (D24)";

        private const string _Decision = "lint_finding";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.85)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static LintFindingDecisionInput BuildInput()
        {
            return new LintFindingDecisionInput
            {
                Mission = new Mission { Id = "msn_test", VesselId = "vsl_test", VoyageId = "vyg_test", Title = "lint a port", Persona = "Linter" },
                Findings = new List<string>
                {
                    "The guard is missing on the fail path.",
                    "Prefer a shorter variable name here.",
                    "Naming differs from the surrounding file."
                }
            };
        }

        // severity levels: cosmetic=0, should_fix=1, must_fix=2, blocks_merge=3.
        private static TypedDecisionResult LintResult(params (int idx, string cls, double severity, double confidence)[] findings)
        {
            Dictionary<string, TypedAnswer> map = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
            foreach ((int idx, string cls, double severity, double confidence) in findings)
            {
                map["class_" + idx] = new TypedAnswer { Type = "choice", Choice = cls, Confidence = confidence };
                map["severity_" + idx] = new TypedAnswer { Type = "score", Score = severity, Confidence = confidence };
            }
            return new TypedDecisionResult { Available = true, Answers = map, InputTokens = 10, OutputTokens = 5, LatencyMs = 12 };
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 100 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        private static TypedLintFindingAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedLintFindingAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Off_ReturnsUnrouted_NoCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LintResult((1, "correctness", 3.0, 0.99)));
                TypedLintFindingAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                LintFindingVerdict result = await adapter.DecideAsync(BuildInput(), LintFindingVerdict.Unrouted(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.HasRouting, "Off routes nothing");
                AssertEqual(0, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Unavailable_ReturnsUnrouted_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("http_529"));
                TypedLintFindingAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                LintFindingVerdict result = await adapter.DecideAsync(BuildInput(), LintFindingVerdict.Unrouted(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.HasRouting, "unavailable routes nothing");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ShadowMode_Correctness_ReturnsUnrouted_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LintResult((1, "correctness", 3.0, 0.99)));
                TypedLintFindingAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));

                LintFindingVerdict result = await adapter.DecideAsync(BuildInput(), LintFindingVerdict.Unrouted(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.HasRouting, "shadow routes nothing");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(1, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Gate_CorrectnessMustFix_AboveThreshold_IsBlocking", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LintResult((1, "correctness", 2.0, 0.97)));
                TypedLintFindingAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                LintFindingVerdict result = await adapter.DecideAsync(BuildInput(), LintFindingVerdict.Unrouted(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.HasRouting, "a correctness must_fix finding routes");
                AssertEqual(1, result.BlockingFindings.Count);
                AssertEqual(0, result.EvidenceNotes.Count);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Gate_SafetyBlocksMerge_IsBlocking", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LintResult((1, "safety", 3.0, 0.95)));
                TypedLintFindingAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                LintFindingVerdict result = await adapter.DecideAsync(BuildInput(), LintFindingVerdict.Unrouted(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, result.BlockingFindings.Count);
            }).ConfigureAwait(false);

            await RunTest("Gate_CorrectnessShouldFix_IsNotBlocking", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // A correctness finding below must_fix (should_fix = 1) does not reach the Judge as blocking.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LintResult((1, "correctness", 1.0, 0.97)));
                TypedLintFindingAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                LintFindingVerdict result = await adapter.DecideAsync(BuildInput(), LintFindingVerdict.Unrouted(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, result.BlockingFindings.Count);
                AssertTrue(!result.HasRouting, "a below-must_fix correctness finding routes nothing and the rule stands");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Gate_StylePreference_BecomesEvidenceNote_NotBlocking", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // A style preference at blocks_merge severity is still only an evidence note, never blocking.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LintResult((1, "style_preference", 3.0, 0.97)));
                TypedLintFindingAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                LintFindingVerdict result = await adapter.DecideAsync(BuildInput(), LintFindingVerdict.Unrouted(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, result.BlockingFindings.Count);
                AssertEqual(1, result.EvidenceNotes.Count);
            }).ConfigureAwait(false);

            await RunTest("Gate_OnlyCorrectnessAndSafetyBlock_StyleIsNote_ConsistencyNeither", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // One safety must_fix (blocking), one style_preference (evidence), one consistency must_fix
                // (neither: consistency never blocks). The Linter's own result is unchanged; only the
                // routing note is derived from these.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LintResult(
                    (1, "safety", 2.0, 0.97), (2, "style_preference", 1.0, 0.9), (3, "consistency", 2.0, 0.9)));
                TypedLintFindingAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                LintFindingVerdict result = await adapter.DecideAsync(BuildInput(), LintFindingVerdict.Unrouted(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, result.BlockingFindings.Count);
                AssertEqual(1, result.EvidenceNotes.Count);
                AssertTrue(result.BlockingFindings.All(item => item.Contains("safety", StringComparison.Ordinal)),
                    "only the safety finding is blocking; consistency and style never block");
            }).ConfigureAwait(false);

            await RunTest("NeverThrows_ClientThrows_ReturnsUnrouted_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                TypedLintFindingAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                LintFindingVerdict result = await adapter.DecideAsync(BuildInput(), LintFindingVerdict.Unrouted(), CancellationToken.None).ConfigureAwait(false);

                AssertTrue(!result.HasRouting, "a client fault leaves the Linter output unchanged");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("CallerTokenReachesClient_AndDecisionPoint", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(LintResult((1, "correctness", 2.0, 0.97)));
                TypedLintFindingAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.DecideAsync(BuildInput(), LintFindingVerdict.Unrouted(), cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter must forward the caller token");
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
            }).ConfigureAwait(false);
        }
    }
}
