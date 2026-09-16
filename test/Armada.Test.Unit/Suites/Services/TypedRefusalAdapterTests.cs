namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
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
    /// Table-driven tests for the D2 <c>refusal</c> adapter: Off keeps the rule with no call;
    /// unavailable and shadow/below-threshold keep the rule and record an event; at or above threshold
    /// the model may promote a prose refusal the rule missed or demote a quoted phrase, but the
    /// structured marker and a provider safeguard block are authoritative and never overturned; the
    /// caller token reaches the client.
    /// </summary>
    public class TypedRefusalAdapterTests : TestSuite
    {
        public override string Name => "Typed Refusal Adapter (D2)";

        private const string _Decision = "refusal";

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = 0.90)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static RefusalDecisionInput BuildInput()
        {
            return new RefusalDecisionInput
            {
                Mission = new Mission { Id = "msn_test", VesselId = "vsl_test", Title = "read a seed-key exchange" },
                AgentOutputTail = "the closing statement",
                MissionTitle = "read a seed-key exchange",
                MarkerPresent = false
            };
        }

        private static TypedDecisionResult OutcomeResult(string outcome, double confidence, double quotedNotOwn = 0.0)
        {
            return new TypedDecisionResult
            {
                Available = true,
                Answers = new Dictionary<string, TypedAnswer>
                {
                    ["outcome"] = new TypedAnswer { Type = "choice", Choice = outcome, Confidence = confidence },
                    ["quoted_not_own"] = new TypedAnswer { Type = "noul", Noul = quotedNotOwn }
                }
            };
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 100 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        private static TypedRefusalAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedRefusalAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        private static CaptainRefusal Refusal(CaptainRefusalKindEnum kind) => new CaptainRefusal { Kind = kind, Reason = "r", Evidence = "e" };

        protected override async Task RunTestsAsync()
        {
            await RunTest("BlockedOnPremise_FilesRedactedBriefContradictionPapercut_RuleStands", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("blocked_on_premise", 0.95));
                TypedRefusalAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                adapter.PapercutDatabase = db.Driver;

                RefusalDecisionInput input = new RefusalDecisionInput
                {
                    Mission = new Mission { Id = "msn_premise1", VesselId = "vsl_test", VoyageId = "vyg_test", Title = "port the decoder", Persona = "Worker" },
                    AgentOutputTail = "Cannot proceed: the brief says PortedDecoder exists at /Volumes/Work/repo/src/PortedDecoder.cs on build.internal.example.com "
                        + "but mission msn_other42 removed it in 1a2b3c4d5e6f; token sk-abcdefghijklmnopqrstuvwxyz0123",
                    MissionTitle = "port the decoder",
                    MarkerPresent = false
                };

                CaptainRefusal result = await adapter.DecideAsync(input, new CaptainRefusal(), CancellationToken.None).ConfigureAwait(false);
                AssertEqual(CaptainRefusalKindEnum.None, result.Kind, "a blocked-on-premise reading never changes the refusal verdict");

                List<ArmadaEvent> stored = await db.Driver.Events.EnumerateByTypeAsync(PapercutParser.EventType, 10).ConfigureAwait(false);
                AssertEqual(1, stored.Count, "exactly one papercut is filed");
                Papercut? papercut = PapercutService.TryFromEvent(stored[0]);
                AssertNotNull(papercut, "the stored event is a readable papercut");
                AssertEqual(PapercutCategoryEnum.BriefContradiction, papercut!.Category, "category");
                AssertEqual("msn_premise1", stored[0].MissionId, "the papercut is scoped to the mission");

                string text = (papercut.Title ?? "") + "\n" + (papercut.Detail ?? "");
                AssertContains("PortedDecoder", text, "the reason survives redaction");
                AssertFalse(text.Contains("/Volumes/Work", StringComparison.Ordinal), "absolute path is redacted");
                AssertFalse(text.Contains("build.internal.example.com", StringComparison.Ordinal), "host is redacted");
                AssertFalse(text.Contains("msn_other42", StringComparison.Ordinal), "Armada id is redacted");
                AssertFalse(text.Contains("1a2b3c4d5e6f", StringComparison.Ordinal), "hash is redacted");
                AssertFalse(text.Contains("sk-abcdefghijklmnopqrstuvwxyz0123", StringComparison.Ordinal), "key is redacted");
            }).ConfigureAwait(false);

            await RunTest("BlockedOnPremiseBelowThreshold_FilesNoPapercut", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("blocked_on_premise", 0.50));
                TypedRefusalAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));
                adapter.PapercutDatabase = db.Driver;

                await adapter.DecideAsync(BuildInput(), new CaptainRefusal(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, await CountEventsAsync(db, PapercutParser.EventType).ConfigureAwait(false), "no papercut below threshold");
            }).ConfigureAwait(false);

            await RunTest("Off_ReturnsRule_NoCall", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("refused_policy", 0.99));
                TypedRefusalAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                CaptainRefusal result = await adapter.DecideAsync(BuildInput(), new CaptainRefusal(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(CaptainRefusalKindEnum.None, result.Kind, "Off must keep the rule");
                AssertEqual(0, client.CallCount);
            }).ConfigureAwait(false);

            await RunTest("Unavailable_ReturnsRule_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(new TypedDecisionResult { Available = false, UnavailableReason = "http_429" });
                TypedRefusalAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                CaptainRefusal result = await adapter.DecideAsync(BuildInput(), new CaptainRefusal(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(CaptainRefusalKindEnum.None, result.Kind, "unavailable must keep the rule");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("ShadowMode_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("refused_policy", 0.99));
                TypedRefusalAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));

                CaptainRefusal result = await adapter.DecideAsync(BuildInput(), new CaptainRefusal(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(CaptainRefusalKindEnum.None, result.Kind, "Shadow must keep the rule even when the model would promote");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("GateBelowThreshold_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("refused_policy", 0.60));
                TypedRefusalAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                CaptainRefusal result = await adapter.DecideAsync(BuildInput(), new CaptainRefusal(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(CaptainRefusalKindEnum.None, result.Kind, "below threshold must keep the rule");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("GateAbove_PromotesProseRefusal_RecordsGated", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("refused_policy", 0.96));
                TypedRefusalAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                CaptainRefusal result = await adapter.DecideAsync(BuildInput(), new CaptainRefusal(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(CaptainRefusalKindEnum.ModelPolicyRefusal, result.Kind, "the model must promote a prose refusal the rule missed");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("GateAbove_DeclaredMarkerIsAuthoritative_NotOverturned", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("completed", 0.99, quotedNotOwn: 0.99));
                TypedRefusalAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                CaptainRefusal result = await adapter.DecideAsync(BuildInput(), Refusal(CaptainRefusalKindEnum.DeclaredRefusal), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(CaptainRefusalKindEnum.DeclaredRefusal, result.Kind, "the structured marker must stay authoritative");
            }).ConfigureAwait(false);

            await RunTest("GateAbove_ProviderSafeguardBlockIsAuthoritative_NotOverturned", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("completed", 0.99, quotedNotOwn: 0.99));
                TypedRefusalAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                CaptainRefusal result = await adapter.DecideAsync(BuildInput(), Refusal(CaptainRefusalKindEnum.ProviderSafeguardBlock), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(CaptainRefusalKindEnum.ProviderSafeguardBlock, result.Kind, "a provider safeguard block must stay authoritative");
            }).ConfigureAwait(false);

            await RunTest("GateAbove_DemotesQuotedPhraseHit", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("completed", 0.97, quotedNotOwn: 0.97));
                TypedRefusalAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                CaptainRefusal result = await adapter.DecideAsync(BuildInput(), Refusal(CaptainRefusalKindEnum.ModelPolicyRefusal), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(CaptainRefusalKindEnum.None, result.Kind, "a quoted phrase hit must be demoted to no-refusal at high confidence");
            }).ConfigureAwait(false);

            await RunTest("GateBelow_DemoteNeedsHighQuoted_KeepsPhraseHit", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("completed", 0.99, quotedNotOwn: 0.80));
                TypedRefusalAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                CaptainRefusal result = await adapter.DecideAsync(BuildInput(), Refusal(CaptainRefusalKindEnum.ModelPolicyRefusal), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(CaptainRefusalKindEnum.ModelPolicyRefusal, result.Kind, "a low quoted_not_own must not demote the phrase hit");
            }).ConfigureAwait(false);

            await RunTest("CallerTokenReachesClient", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(OutcomeResult("refused_policy", 0.96));
                TypedRefusalAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.DecideAsync(BuildInput(), new CaptainRefusal(), cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter must forward the caller token");
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
            }).ConfigureAwait(false);
        }
    }
}
