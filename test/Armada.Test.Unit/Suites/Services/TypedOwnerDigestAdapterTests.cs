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
    /// Tests for the D13 <c>owner_digest</c> adapter and its scheduled runner.
    ///
    /// Adapter half (table-driven, shared skeleton): Off keeps the deterministic cost with no call;
    /// unavailable and below-threshold keep it and record one event; a gate at or above threshold may
    /// only ESCALATE the cost and records a gated event; the caller token reaches the client for
    /// timeout linking.
    ///
    /// Runner half: the runner is dormant while the decision is Off (never forced on); it is a no-op
    /// when the hit source is empty; when candidates exist it posts exactly ONE owner-addressed note
    /// and emits exactly ONE <c>owner_decisions.digest</c> event, ranked by cost, at most once per UTC
    /// day; and it NEVER answers a question — the note lists proposed defaults as suggestions and the
    /// digest event carries only ranking metadata, no answer.
    /// </summary>
    public class TypedOwnerDigestAdapterTests : TestSuite
    {
        public override string Name => "Typed Owner Digest (D13)";

        private const string _Decision = "owner_digest";
        private const double _Threshold = 0.80;

        #region Builders

        private static TypedDecisionSettings BuildSettings(TypedDecisionModeEnum decisionMode, double threshold = _Threshold)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = TypedDecisionModeEnum.Gate };
            settings.Decisions[_Decision] = new TypedDecisionRuleSettings { Mode = decisionMode, GateThreshold = threshold };
            return settings;
        }

        private static OwnerDigestCandidate BuildCandidate(
            string question = "Should the Eaton reduction be approved?",
            int chain = 0,
            double ageHours = 1.0,
            string proposedDefault = "hold the row",
            string source = "preflight_q13",
            string? vesselId = "vsl_example")
        {
            return new OwnerDigestCandidate
            {
                QuestionText = question,
                BlockedRow = "row one",
                ChainCount = chain,
                AgeHours = ageHours,
                ProposedDefault = proposedDefault,
                Source = source,
                VesselId = vesselId
            };
        }

        private static TypedDecisionResult CostResult(int level, double confidence, double defaultSafe = 0.0)
        {
            return new TypedDecisionResult
            {
                Available = true,
                Answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
                {
                    ["cost_of_waiting"] = new TypedAnswer { Type = "score", Score = level, Confidence = confidence },
                    ["default_safe"] = new TypedAnswer { Type = "noul", Noul = defaultSafe }
                },
                InputTokens = 20,
                OutputTokens = 6,
                LatencyMs = 30
            };
        }

        private static TypedOwnerDigestAdapter BuildAdapter(TestDatabase db, FakeTypedDecisionClient client, TypedDecisionSettings settings)
        {
            return new TypedOwnerDigestAdapter(client, new TypedDecisionRecorder(db.Driver, new LoggingModule()), settings, new LoggingModule());
        }

        private static async Task<int> CountEventsAsync(TestDatabase db, string eventType)
        {
            EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                .EnumerateAsync(new EnumerationQuery { EventType = eventType, PageNumber = 1, PageSize = 100 })
                .ConfigureAwait(false);
            return events.Objects.Count;
        }

        private static OwnerDigestHitSource SourceOf(params OwnerDigestCandidate[] candidates)
        {
            IReadOnlyList<OwnerDigestCandidate> list = new List<OwnerDigestCandidate>(candidates);
            return _ => Task.FromResult(list);
        }

        private OwnerDigestRunner BuildRunner(
            TestDatabase db,
            FakeTypedDecisionClient client,
            TypedDecisionSettings settings,
            FakeOwnerDecisionNotePoster poster,
            OwnerDigestHitSource source,
            Func<DateTime> nowUtc)
        {
            TypedOwnerDigestAdapter adapter = BuildAdapter(db, client, settings);
            return new OwnerDigestRunner(settings, adapter, poster, source, db.Driver, nowUtc, new LoggingModule());
        }

        #endregion

        protected override async Task RunTestsAsync()
        {
            // ---- Adapter half ----

            await RunTest("Adapter_Off_ReturnsRule_NoCallNoEvent", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CostResult(3, 0.99));
                TypedOwnerDigestAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Off));

                OwnerDigestCandidate candidate = BuildCandidate();
                OwnerDigestEntry rule = TypedOwnerDigestAdapter.DeterministicRule(candidate);
                OwnerDigestEntry result = await adapter.DecideAsync(candidate, rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(rule.CostLevel, result.CostLevel, "Off must keep the deterministic cost");
                AssertEqual("rule", result.Outcome);
                AssertEqual(0, client.CallCount, "Off must not call the client");
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Adapter_Unavailable_ReturnsRule_RecordsUnavailable", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(FakeTypedDecisionClient.Unavailable("timeout"));
                TypedOwnerDigestAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                OwnerDigestCandidate candidate = BuildCandidate(chain: 5);
                OwnerDigestEntry rule = TypedOwnerDigestAdapter.DeterministicRule(candidate);
                OwnerDigestEntry result = await adapter.DecideAsync(candidate, rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(rule.CostLevel, result.CostLevel, "unavailable must keep the deterministic cost");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Adapter_ClientThrows_ReturnsRule_NeverBreaksCaller", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                TypedOwnerDigestAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                OwnerDigestCandidate candidate = BuildCandidate();
                OwnerDigestEntry rule = TypedOwnerDigestAdapter.DeterministicRule(candidate);
                OwnerDigestEntry result = await adapter.DecideAsync(candidate, rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(rule.CostLevel, result.CostLevel, "a throwing client must never break the caller");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeUnavailable).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Adapter_BelowThreshold_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // A low-age, no-chain candidate is deterministically cost 0. The model reads cost 3 but
                // below the threshold, so the rule stands and only a shadow event is recorded.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CostResult(3, 0.50));
                TypedOwnerDigestAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                OwnerDigestCandidate candidate = BuildCandidate(chain: 0, ageHours: 1.0);
                OwnerDigestEntry rule = TypedOwnerDigestAdapter.DeterministicRule(candidate);
                OwnerDigestEntry result = await adapter.DecideAsync(candidate, rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, rule.CostLevel, "a fresh, unchained candidate is deterministically cost 0");
                AssertEqual(rule.CostLevel, result.CostLevel, "below threshold must keep the rule cost");
                AssertEqual("rule", result.Outcome);
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Adapter_ShadowMode_ReturnsRule_RecordsShadow", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CostResult(3, 0.99));
                TypedOwnerDigestAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Shadow));

                OwnerDigestCandidate candidate = BuildCandidate(chain: 0, ageHours: 1.0);
                OwnerDigestEntry rule = TypedOwnerDigestAdapter.DeterministicRule(candidate);
                OwnerDigestEntry result = await adapter.DecideAsync(candidate, rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(rule.CostLevel, result.CostLevel, "Shadow must keep the rule even at high confidence");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false));
                AssertEqual(0, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Adapter_GateAboveThreshold_EscalatesCost_RecordsGated", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Deterministic cost 0; the model reads a held landing (cost 3) at high confidence, so the
                // gate ESCALATES the cost and records a gated event.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CostResult(3, 0.95, defaultSafe: 0.95));
                TypedOwnerDigestAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                OwnerDigestCandidate candidate = BuildCandidate(chain: 0, ageHours: 1.0);
                OwnerDigestEntry rule = TypedOwnerDigestAdapter.DeterministicRule(candidate);
                OwnerDigestEntry result = await adapter.DecideAsync(candidate, rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(3, result.CostLevel, "the gated model must escalate the cost to a held landing");
                AssertEqual("escalated", result.Outcome);
                AssertTrue(result.DefaultSafe, "default_safe at 0.95 must annotate the entry");
                AssertEqual(1, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeGated).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Adapter_DefaultSafeConfidenceWithoutNoul_DoesNotAnnotate", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                TypedDecisionResult confidenceOnly = new TypedDecisionResult
                {
                    Available = true,
                    Answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
                    {
                        ["cost_of_waiting"] = new TypedAnswer { Type = "score", Score = 3, Confidence = 0.95 },
                        ["default_safe"] = new TypedAnswer { Type = "noul", Confidence = 0.95 }
                    }
                };
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(confidenceOnly);
                TypedOwnerDigestAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                OwnerDigestCandidate candidate = BuildCandidate(chain: 0, ageHours: 1.0);
                OwnerDigestEntry result = await adapter.DecideAsync(candidate, TypedOwnerDigestAdapter.DeterministicRule(candidate), CancellationToken.None).ConfigureAwait(false);

                AssertFalse(result.DefaultSafe, "a confidence is not the probability that the default is safe");
            }).ConfigureAwait(false);

            await RunTest("Adapter_GateNeverLowersCost", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Deterministic cost 3 (long chain); the model reads a lower cost 1 at high confidence.
                // The gate may only escalate, so the cost stays 3.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CostResult(1, 0.99));
                TypedOwnerDigestAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                OwnerDigestCandidate candidate = BuildCandidate(chain: 5, ageHours: 1.0);
                OwnerDigestEntry rule = TypedOwnerDigestAdapter.DeterministicRule(candidate);
                OwnerDigestEntry result = await adapter.DecideAsync(candidate, rule, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(3, rule.CostLevel, "a five-deep chain is deterministically cost 3");
                AssertEqual(3, result.CostLevel, "the gate must never lower the deterministic cost");
            }).ConfigureAwait(false);

            await RunTest("Adapter_ForwardsCallerToken_AndAsksBothQuestions", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CostResult(2, 0.95));
                TypedOwnerDigestAdapter adapter = BuildAdapter(db, client, BuildSettings(TypedDecisionModeEnum.Gate));

                using CancellationTokenSource cts = new CancellationTokenSource();
                OwnerDigestCandidate candidate = BuildCandidate();
                await adapter.DecideAsync(candidate, TypedOwnerDigestAdapter.DeterministicRule(candidate), cts.Token).ConfigureAwait(false);

                AssertTrue(client.LastToken == cts.Token, "adapter must forward the caller token so the client links its timeout to it");
                AssertNotNull(client.LastRequest);
                AssertEqual(_Decision, client.LastRequest!.DecisionPoint);
                AssertTrue(client.LastRequest.Questions.ContainsKey("cost_of_waiting"), "questions must include cost_of_waiting");
                AssertTrue(client.LastRequest.Questions.ContainsKey("default_safe"), "questions must include default_safe");
            }).ConfigureAwait(false);

            await RunTest("Adapter_DeterministicRule_ScalesWithChainAndAge", () =>
            {
                AssertEqual(0, TypedOwnerDigestAdapter.DeterministicRule(BuildCandidate(chain: 0, ageHours: 1.0)).CostLevel);
                AssertEqual(1, TypedOwnerDigestAdapter.DeterministicRule(BuildCandidate(chain: 0, ageHours: 6.0)).CostLevel);
                AssertEqual(2, TypedOwnerDigestAdapter.DeterministicRule(BuildCandidate(chain: 1, ageHours: 1.0)).CostLevel);
                AssertEqual(3, TypedOwnerDigestAdapter.DeterministicRule(BuildCandidate(chain: 3, ageHours: 1.0)).CostLevel);
                AssertEqual(3, TypedOwnerDigestAdapter.DeterministicRule(BuildCandidate(chain: 0, ageHours: 72.0)).CostLevel);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            // ---- Runner half ----

            await RunTest("Runner_DecisionOff_Dormant_PostsNothing", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CostResult(3, 0.99));
                FakeOwnerDecisionNotePoster poster = new FakeOwnerDecisionNotePoster();
                TypedDecisionSettings settings = BuildSettings(TypedDecisionModeEnum.Off);
                OwnerDigestRunner runner = BuildRunner(db, client, settings, poster,
                    SourceOf(BuildCandidate(chain: 5)), () => new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc));

                OwnerDigestRunResult result = await runner.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

                AssertFalse(result.Posted, "an Off decision must leave the runner dormant");
                AssertEqual("dormant", result.Reason);
                AssertEqual(0, poster.Posts.Count, "a dormant runner must post no note");
                AssertEqual(0, client.CallCount, "a dormant runner must not call the client");
                AssertEqual(0, await CountEventsAsync(db, OwnerDigestRunner.DigestEventType).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Runner_NoCandidates_NoOp", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CostResult(3, 0.99));
                FakeOwnerDecisionNotePoster poster = new FakeOwnerDecisionNotePoster();
                TypedDecisionSettings settings = BuildSettings(TypedDecisionModeEnum.Gate);
                OwnerDigestRunner runner = BuildRunner(db, client, settings, poster,
                    SourceOf(), () => new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc));

                OwnerDigestRunResult result = await runner.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

                AssertFalse(result.Posted, "no candidates must post nothing");
                AssertEqual("no_candidates", result.Reason);
                AssertEqual(0, poster.Posts.Count);
                AssertEqual(0, await CountEventsAsync(db, OwnerDigestRunner.DigestEventType).ConfigureAwait(false));
            }).ConfigureAwait(false);

            await RunTest("Runner_PostsOneNoteAndOneEvent_RankedByCost", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Gate mode with a client that scores every candidate at the same level; deterministic
                // cost then decides the rank. The high-chain question must come first.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CostResult(0, 0.10));
                FakeOwnerDecisionNotePoster poster = new FakeOwnerDecisionNotePoster();
                TypedDecisionSettings settings = BuildSettings(TypedDecisionModeEnum.Gate);
                OwnerDigestCandidate low = BuildCandidate(question: "low cost question", chain: 0, ageHours: 1.0, source: "board_question");
                OwnerDigestCandidate high = BuildCandidate(question: "high cost question", chain: 5, ageHours: 30.0, source: "preflight_q13");
                OwnerDigestRunner runner = BuildRunner(db, client, settings, poster,
                    SourceOf(low, high), () => new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc));

                OwnerDigestRunResult result = await runner.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Posted, "candidates must produce a post");
                AssertEqual(2, result.CandidateCount);
                AssertEqual(1, client.Calls, "independent candidates are ranked in one request");
                AssertEqual(2, await CountEventsAsync(db, TypedDecisionRecorder.EventTypeShadow).ConfigureAwait(false),
                    "each candidate still records its own decision event");
                AssertEqual(1, poster.Posts.Count, "exactly one owner-addressed note per pass");
                AssertEqual(1, await CountEventsAsync(db, OwnerDigestRunner.DigestEventType).ConfigureAwait(false));
                string note = poster.Posts[0];
                AssertTrue(note.IndexOf("high cost question", StringComparison.Ordinal)
                    < note.IndexOf("low cost question", StringComparison.Ordinal),
                    "the higher-cost question must be ranked first");
            }).ConfigureAwait(false);

            await RunTest("Runner_OneNotePerDay_SecondPassSameDayNoOp", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CostResult(2, 0.90));
                FakeOwnerDecisionNotePoster poster = new FakeOwnerDecisionNotePoster();
                TypedDecisionSettings settings = BuildSettings(TypedDecisionModeEnum.Gate);
                DateTime day = new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc);
                DateTime clockValue = day;
                OwnerDigestRunner runner = BuildRunner(db, client, settings, poster,
                    SourceOf(BuildCandidate(chain: 2)), () => clockValue);

                OwnerDigestRunResult first = await runner.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
                clockValue = day.AddHours(6); // same UTC day, later time
                OwnerDigestRunResult second = await runner.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

                AssertTrue(first.Posted, "the first pass of the day must post");
                AssertFalse(second.Posted, "a second pass on the same UTC day must not post");
                AssertEqual("already_posted_today", second.Reason);
                AssertEqual(1, poster.Posts.Count, "exactly one note per UTC day");
                AssertEqual(1, await CountEventsAsync(db, OwnerDigestRunner.DigestEventType).ConfigureAwait(false));

                // The next UTC day posts again.
                clockValue = day.AddDays(1);
                OwnerDigestRunResult third = await runner.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
                AssertTrue(third.Posted, "the next UTC day must post again");
                AssertEqual(2, poster.Posts.Count);
            }).ConfigureAwait(false);

            await RunTest("Runner_NeverAnswers_NoteIsProposalAndEventCarriesNoAnswer", async () =>
            {
                using TestDatabase db = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // default_safe is high, yet the runner still only PROPOSES; it answers nothing.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(CostResult(1, 0.95, defaultSafe: 0.99));
                FakeOwnerDecisionNotePoster poster = new FakeOwnerDecisionNotePoster();
                TypedDecisionSettings settings = BuildSettings(TypedDecisionModeEnum.Gate);
                OwnerDigestRunner runner = BuildRunner(db, client, settings, poster,
                    SourceOf(BuildCandidate(question: "Approve the reduction?", proposedDefault: "hold")),
                    () => new DateTime(2026, 9, 16, 8, 0, 0, DateTimeKind.Utc));

                OwnerDigestRunResult result = await runner.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

                AssertTrue(result.Posted);
                string note = poster.Posts[0];
                AssertContains("no question is answered here", note);
                AssertContains("Proposed default", note);

                // The digest event carries only ranking metadata: no answer, choice, or ruling field.
                EnumerationResult<ArmadaEvent> events = await db.Driver.Events
                    .EnumerateAsync(new EnumerationQuery { EventType = OwnerDigestRunner.DigestEventType, PageNumber = 1, PageSize = 10 })
                    .ConfigureAwait(false);
                AssertEqual(1, events.Objects.Count);
                string payload = events.Objects[0].Payload ?? "";
                AssertFalse(payload.Contains("\"answer\"", StringComparison.Ordinal), "the digest event must not carry an answer");
                AssertFalse(payload.Contains("\"choice\"", StringComparison.Ordinal), "the digest event must not carry a choice");
                AssertFalse(payload.Contains("\"ruling\"", StringComparison.Ordinal), "the digest event must not carry a ruling");
            }).ConfigureAwait(false);
        }
    }
}
