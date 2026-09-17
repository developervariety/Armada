namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Table-driven tests for the D11 inbox_triage adapter. Triage is additive and non-destructive:
    /// NOTHING is hidden or dropped. Off makes no call and leaves the deterministic order; an
    /// unavailable model records one unavailable event and leaves the order; a below-threshold answer
    /// and Shadow mode record a shadow event per item and leave the order; a Gate answer at or above
    /// threshold annotates each item with an attention label and re-sorts by it. Every case proves the
    /// same items are returned (nothing hidden). A dedicated case proves the re-sort and that a board
    /// note is classified with a kind. The adapter never throws and forwards the caller's token.
    /// </summary>
    public class InboxTriageAdapterTests : TestSuite
    {
        public override string Name => "Inbox Triage Adapter (D11)";

        private const double _Threshold = 0.80;

        protected override async Task RunTestsAsync()
        {
            List<TriageCase> cases = new List<TriageCase>
            {
                new TriageCase
                {
                    Name = "Off_NoCall_DeterministicOrder",
                    GlobalMode = TypedDecisionModeEnum.Off,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Attention(3, 0.95),
                    ExpectCalls = 0,
                    ExpectEventType = null,
                    ExpectEventCount = 0,
                    ExpectAnnotated = false
                },
                new TriageCase
                {
                    Name = "Unavailable_DeterministicOrder_UnavailableEvent",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = FakeTypedDecisionClient.Unavailable("timeout"),
                    ExpectCalls = 1,
                    ExpectEventType = TypedDecisionRecorder.EventTypeUnavailable,
                    ExpectEventCount = 1,
                    ExpectAnnotated = false
                },
                new TriageCase
                {
                    Name = "BelowThreshold_DeterministicOrder_ShadowEvents",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Attention(2, 0.40),
                    ExpectCalls = 1,
                    ExpectEventType = TypedDecisionRecorder.EventTypeShadow,
                    ExpectEventCount = 3,
                    ExpectAnnotated = false
                },
                new TriageCase
                {
                    Name = "GateAboveThreshold_Annotated_GatedEvents",
                    GlobalMode = TypedDecisionModeEnum.Gate,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Attention(3, 0.95),
                    ExpectCalls = 1,
                    ExpectEventType = TypedDecisionRecorder.EventTypeGated,
                    ExpectEventCount = 3,
                    ExpectAnnotated = true
                },
                new TriageCase
                {
                    Name = "ShadowMode_DeterministicOrder_ShadowEvents",
                    GlobalMode = TypedDecisionModeEnum.Shadow,
                    DecisionMode = TypedDecisionModeEnum.Gate,
                    Result = Attention(3, 0.95),
                    ExpectCalls = 1,
                    ExpectEventType = TypedDecisionRecorder.EventTypeShadow,
                    ExpectEventCount = 3,
                    ExpectAnnotated = false
                }
            };

            foreach (TriageCase testCase in cases)
            {
                await RunTest("InboxTriage_" + testCase.Name, async () =>
                {
                    using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                    FakeTypedDecisionClient client = new FakeTypedDecisionClient(testCase.Result);
                    InboxTriageAdapter adapter = BuildAdapter(testDb.Driver, client, testCase.GlobalMode, testCase.DecisionMode);

                    List<InboxItem> input = SeededItems();
                    List<string> inputIds = input.Select(item => item.EntityId!).ToList();

                    List<InboxItem> output = await adapter.TriageInboxAsync(input, CancellationToken.None).ConfigureAwait(false);

                    AssertEqual(testCase.ExpectCalls, client.Calls, "client call count");

                    // NOTHING is hidden: every input item is present in the output.
                    AssertEqual(inputIds.Count, output.Count, "no item is dropped");
                    foreach (string id in inputIds)
                        AssertTrue(output.Any(item => item.EntityId == id), "input item " + id + " is still present");

                    if (testCase.ExpectAnnotated)
                    {
                        AssertTrue(output.All(item => !String.IsNullOrEmpty(item.Attention)), "every item gets an attention label in Gate mode");
                    }
                    else
                    {
                        AssertTrue(output.All(item => String.IsNullOrEmpty(item.Attention)), "no attention label outside a gated call");
                        // The deterministic order (by severity) is preserved.
                        AssertTrue(output.Select(item => item.EntityId).SequenceEqual(inputIds), "the deterministic order is preserved");
                    }

                    List<ArmadaEvent> typed = await AllTypedDecisionEventsAsync(testDb.Driver).ConfigureAwait(false);
                    if (testCase.ExpectEventType == null)
                    {
                        AssertEqual(0, typed.Count, "no typed-decision event when the decision is off");
                    }
                    else
                    {
                        AssertEqual(testCase.ExpectEventCount, typed.Count, "typed-decision event count");
                        AssertTrue(typed.All(evt => evt.EventType == testCase.ExpectEventType), "every event is the expected type");
                    }
                });
            }

            await RunTest("InboxTriage_GateReordersByAttention_NothingHidden", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                // Give the lowest-severity item the highest attention, so a re-sort is observable and
                // could only happen if the adapter re-ordered by attention rather than severity.
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(request =>
                {
                    string state = FakeTypedDecisionClient.StateText(request);
                    int score = state.Contains("stalled_captain", StringComparison.Ordinal) ? 3
                        : state.Contains("review", StringComparison.Ordinal) ? 1
                        : 0;
                    return Attention(score, 0.95);
                });
                InboxTriageAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                List<InboxItem> output = await adapter.TriageInboxAsync(SeededItems(), CancellationToken.None).ConfigureAwait(false);

                AssertEqual(3, output.Count, "nothing is hidden by the re-sort");
                AssertEqual("stalled_captain", output[0].Kind, "the highest-attention item sorts first even though it is a lower severity");
                AssertEqual("blocking_live_voyage", output[0].Attention, "the highest-attention item carries the top attention label");
            });

            await RunTest("InboxTriage_BoardNotes_Classified_GatedEvents", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(AttentionAndKind(2, 0.95, "question"));
                InboxTriageAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                List<BoardNoteTriageInput> notes = new List<BoardNoteTriageInput>
                {
                    new BoardNoteTriageInput { Id = "cmsg_1", AuthorType = "Operator", Content = "Where does the porting campaign stand?" }
                };

                IReadOnlyList<BoardNoteTriage> triaged = await adapter.TriageBoardNotesAsync(notes, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, triaged.Count, "the note is triaged in Gate mode");
                AssertEqual("cmsg_1", triaged[0].Id, "the triage is keyed by note id");
                AssertEqual("question", triaged[0].NoteKind, "the note kind is classified");
                AssertTrue(!String.IsNullOrEmpty(triaged[0].Attention), "the note carries an attention label");
            });

            await RunTest("InboxTriage_ForwardsCallerTokenToClient", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = new FakeTypedDecisionClient(Attention(1, 0.20));
                InboxTriageAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                using CancellationTokenSource cts = new CancellationTokenSource();
                await adapter.TriageInboxAsync(SeededItems(), cts.Token).ConfigureAwait(false);

                AssertTrue(client.Calls >= 1, "the client was called");
                AssertTrue(client.LastToken.Equals(cts.Token), "the adapter forwards the caller's token so the client timeout applies");
            });

            await RunTest("InboxTriage_ClientThrows_NeverThrows_DeterministicOrder", async () =>
            {
                using TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false);
                FakeTypedDecisionClient client = FakeTypedDecisionClient.Throwing(new InvalidOperationException("boom"));
                InboxTriageAdapter adapter = BuildAdapter(testDb.Driver, client, TypedDecisionModeEnum.Gate, TypedDecisionModeEnum.Gate);

                List<InboxItem> input = SeededItems();
                List<string> inputIds = input.Select(item => item.EntityId!).ToList();

                // Must not throw.
                List<InboxItem> output = await adapter.TriageInboxAsync(input, CancellationToken.None).ConfigureAwait(false);

                AssertTrue(output.Select(item => item.EntityId).SequenceEqual(inputIds), "a client fault leaves the deterministic order, nothing hidden");
                AssertTrue(output.All(item => String.IsNullOrEmpty(item.Attention)), "a client fault annotates nothing");
            });
        }

        private static InboxTriageAdapter BuildAdapter(
            DatabaseDriver database,
            FakeTypedDecisionClient client,
            TypedDecisionModeEnum globalMode,
            TypedDecisionModeEnum decisionMode)
        {
            TypedDecisionSettings settings = new TypedDecisionSettings { Mode = globalMode };
            settings.Decisions[InboxTriageAdapter.DecisionPoint].Mode = decisionMode;
            settings.Decisions[InboxTriageAdapter.DecisionPoint].GateThreshold = _Threshold;
            TypedDecisionRecorder recorder = new TypedDecisionRecorder(database, new LoggingModule());
            return new InboxTriageAdapter(settings, client, recorder, new LoggingModule());
        }

        // Deterministic severity order: Critical first, then two Warnings by title.
        private static List<InboxItem> SeededItems()
        {
            return new List<InboxItem>
            {
                new InboxItem { Kind = "merge_failed", Severity = InboxSeverityEnum.Critical, Title = "Merge failed: master", EntityType = "merge_entry", EntityId = "mrg_1" },
                new InboxItem { Kind = "review", Severity = InboxSeverityEnum.Warning, Title = "Review: port the decoder", EntityType = "mission", EntityId = "msn_1" },
                new InboxItem { Kind = "stalled_captain", Severity = InboxSeverityEnum.Warning, Title = "Stalled captain: worker-2", EntityType = "captain", EntityId = "cpt_1" }
            };
        }

        private static TypedDecisionResult Attention(int scoreIndex, double confidence)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                ["needs_human_now"] = new TypedAnswer { Type = "score", Score = scoreIndex, Confidence = confidence }
            };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 8, OutputTokens = 4, LatencyMs = 10 };
        }

        private static TypedDecisionResult AttentionAndKind(int scoreIndex, double confidence, string noteKind)
        {
            Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal)
            {
                ["needs_human_now"] = new TypedAnswer { Type = "score", Score = scoreIndex, Confidence = confidence },
                ["note_kind"] = new TypedAnswer { Type = "choice", Choice = noteKind, Confidence = confidence }
            };
            return new TypedDecisionResult { Available = true, Answers = answers, InputTokens = 8, OutputTokens = 4, LatencyMs = 10 };
        }

        private static async Task<List<ArmadaEvent>> AllTypedDecisionEventsAsync(DatabaseDriver database)
        {
            List<ArmadaEvent> all = new List<ArmadaEvent>();
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeGated, 50).ConfigureAwait(false));
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeShadow, 50).ConfigureAwait(false));
            all.AddRange(await database.Events.EnumerateByTypeAsync(TypedDecisionRecorder.EventTypeUnavailable, 50).ConfigureAwait(false));
            return all;
        }

        private sealed class TriageCase
        {
            public required string Name { get; init; }
            public required TypedDecisionModeEnum GlobalMode { get; init; }
            public required TypedDecisionModeEnum DecisionMode { get; init; }
            public required TypedDecisionResult Result { get; init; }
            public required int ExpectCalls { get; init; }
            public required string? ExpectEventType { get; init; }
            public required int ExpectEventCount { get; init; }
            public required bool ExpectAnnotated { get; init; }
        }
    }
}
