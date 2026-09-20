namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Test.Common;

    public class TypedDecisionBatcherTests : TestSuite
    {
        public override string Name => "Typed Decision Batcher";

        protected override async Task RunTestsAsync()
        {
            await RunTest("DecideAll_QuestionText_CountsTowardTheRequest_SoABatchSplits", async () =>
            {
                // Two items whose STATE fits one batch easily but whose question text together exceeds the
                // request budget. Counting state alone sent them as one request larger than the provider takes.
                EchoClient client = new EchoClient();
                List<TypedDecisionBatchItem> items = new List<TypedDecisionBatchItem> { WordyItem("first"), WordyItem("second") };
                AssertTrue(TypedDecisionBatcher.QuestionChars(items[0].Questions) * 2 > TypedDecisionBatcher.MaxRequestChars,
                    "the fixture's questions together exceed the request budget");
                AssertTrue(TypedDecisionBatcher.QuestionChars(items[0].Questions) < TypedDecisionBatcher.MaxRequestChars,
                    "each item alone fits");

                List<TypedDecisionResult> results = await TypedDecisionBatcher.DecideAllAsync(
                    client, "inbox_triage", items, 60000, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(2, client.Requests.Count, "the batch splits on question text, not only on state");
                AssertEqual(2, results.Count, "each item still gets its answer");
            });

            await RunTest("QuestionChars_CountsInstructionsAndEveryOption", () =>
            {
                Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
                {
                    ["n"] = new NoulQuestion("12345", TrueMeaning: "abc", FalseMeaning: "de"),
                    ["c"] = new ChoiceQuestion("1234", new Dictionary<string, string> { ["k"] = "xyz" }),
                    ["s"] = new ScoreQuestion("123", new List<string> { "lo", "hi" })
                };
                // keys (1+1+1) + noul (5+3+2) + choice (4 + 1+3) + score (3 + 2+2)
                AssertEqual(3 + 10 + 8 + 7, TypedDecisionBatcher.QuestionChars(questions));
            });

            await RunTest("DecideAll_OneItem_SendsTheItemUnwrapped", async () =>
            {
                EchoClient client = new EchoClient();
                TypedDecisionBatchItem item = Item("only item", 2);

                List<TypedDecisionResult> results = await TypedDecisionBatcher.DecideAllAsync(
                    client, "criteria_lint", new List<TypedDecisionBatchItem> { item }, 8000, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, client.Requests.Count);
                AssertTrue(ReferenceEquals(item.State.State, client.Requests[0].State), "a lone item is sent as its own state");
                AssertTrue(client.Requests[0].Questions.ContainsKey("q1"), "a lone item keeps its own question keys");
                AssertEqual(1, results.Count);
                AssertEqual(1, results[0].BatchSize);
                AssertEqual(0.5, results[0].Answers["q1"].Noul);
            });

            await RunTest("DecideAll_ManyItems_OneRequestScopedQuestionsSplitAnswers", async () =>
            {
                EchoClient client = new EchoClient { InputTokens = 301, OutputTokens = 30 };
                List<TypedDecisionBatchItem> items = new List<TypedDecisionBatchItem> { Item("first", 2), Item("second", 2), Item("third", 2) };

                List<TypedDecisionResult> results = await TypedDecisionBatcher.DecideAllAsync(
                    client, "inbox_triage", items, 8000, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, client.Requests.Count, "independent items share one request");
                TypedDecisionRequest sent = client.Requests[0];
                AssertEqual(6, sent.Questions.Count);
                AssertTrue(sent.Questions.ContainsKey("item2__q1"), "questions are keyed by item");
                AssertTrue(sent.Questions["item2__q1"].Instructions.StartsWith("This question's state is `items[1]` only.", StringComparison.Ordinal),
                    "each question names its 0-based items path");
                AssertTrue(sent.Questions["item2__q1"] is NoulQuestion, "the question keeps its type");

                AssertPackedItems(sent.State, 3);

                AssertEqual(3, results.Count);
                for (int i = 0; i < 3; i++)
                {
                    AssertTrue(results[i].Available, "item result available");
                    AssertEqual(3, results[i].BatchSize);
                    AssertEqual(2, results[i].Answers.Count, "each item receives only its own answers");
                    AssertTrue(results[i].Answers.ContainsKey("q1"), "answers are keyed as the item asked them");
                }
                AssertEqual(301, results[0].InputTokens + results[1].InputTokens + results[2].InputTokens, "token shares sum to the request");
                AssertEqual(30, results[0].OutputTokens + results[1].OutputTokens + results[2].OutputTokens, "token shares sum to the request");
                AssertEqual("jev-test", results[2].Model);
            });

            await RunTest("DecideAll_QuestionLimit_SplitsIntoRequestsOfAtMostOneHundred", async () =>
            {
                EchoClient client = new EchoClient();
                List<TypedDecisionBatchItem> items = new List<TypedDecisionBatchItem>();
                for (int i = 0; i < 30; i++) items.Add(Item("criterion " + i, 5));

                List<TypedDecisionResult> results = await TypedDecisionBatcher.DecideAllAsync(
                    client, "criteria_lint", items, 100000, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(2, client.Requests.Count, "150 questions need two requests");
                AssertEqual(100, client.Requests[0].Questions.Count);
                AssertEqual(50, client.Requests[1].Questions.Count);
                AssertEqual(30, results.Count);
                AssertEqual(5, results[29].Answers.Count);
            });

            await RunTest("DecideAll_StateBudget_NeverSendsMoreStateThanOneDecisionMay", async () =>
            {
                EchoClient client = new EchoClient();
                List<TypedDecisionBatchItem> items = new List<TypedDecisionBatchItem>
                {
                    Item(Words(3000), 1), Item(Words(3000), 1), Item(Words(3000), 1)
                };
                AssertTrue(items[0].State.Text.Length >= 3000, "each item carries its full state");

                List<TypedDecisionResult> results = await TypedDecisionBatcher.DecideAllAsync(
                    client, "followup_routing", items, 8000, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(2, client.Requests.Count, "the third item would push the request past the state budget");
                AssertEqual(2, results[0].BatchSize);
                AssertEqual(1, results[2].BatchSize);
            });

            await RunTest("DecideAll_UnavailableRequest_StopsAndMarksEveryRemainingItem", async () =>
            {
                EchoClient client = new EchoClient { FailReason = "http_429" };
                List<TypedDecisionBatchItem> items = new List<TypedDecisionBatchItem>();
                for (int i = 0; i < 30; i++) items.Add(Item("criterion " + i, 5));

                List<TypedDecisionResult> results = await TypedDecisionBatcher.DecideAllAsync(
                    client, "criteria_lint", items, 100000, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(1, client.Requests.Count, "no further request after the provider is unavailable");
                AssertEqual(30, results.Count);
                AssertFalse(results[29].Available, "later items report the unavailable result");
                AssertEqual("http_429", results[0].UnavailableReason);
            });

            await RunTest("DecideAll_ClientThrows_ReturnsUnavailableNeverThrows", async () =>
            {
                EchoClient client = new EchoClient { Throw = true };
                List<TypedDecisionResult> results = await TypedDecisionBatcher.DecideAllAsync(
                    client, "criteria_lint", new List<TypedDecisionBatchItem> { Item("x", 1), Item("y", 1) }, 8000, CancellationToken.None).ConfigureAwait(false);

                AssertEqual(2, results.Count);
                AssertEqual("exception", results[1].UnavailableReason);
            });
        }

        private void AssertPackedItems(object state, int expectedCount)
        {
            Dictionary<string, object?> packed = state as Dictionary<string, object?>
                ?? throw new InvalidOperationException("batched state is an object with an items array");
            AssertTrue(packed.ContainsKey("items"), "batched state is { items: [...] }");
            System.Collections.IList list = packed["items"] as System.Collections.IList
                ?? throw new InvalidOperationException("items is a list of item states");
            AssertEqual(expectedCount, list.Count, "one array entry per batched item, no wrapper object");
        }

        private static string Words(int length)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(length + 8);
            while (sb.Length < length) sb.Append("gear ");
            return sb.ToString(0, length);
        }

        private static TypedDecisionBatchItem WordyItem(string text)
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                ["q1"] = new NoulQuestion(new string('w', 45000))
            };
            return new TypedDecisionBatchItem(DecisionStateRedactor.RedactState(new Dictionary<string, object?> { ["text"] = text }, 100000), questions);
        }

        private static TypedDecisionBatchItem Item(string text, int questionCount)
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            for (int i = 1; i <= questionCount; i++) questions["q" + i] = new NoulQuestion("Is statement " + i + " true?");
            return new TypedDecisionBatchItem(DecisionStateRedactor.RedactState(new Dictionary<string, object?> { ["text"] = text }, 100000), questions);
        }

        private sealed class EchoClient : ITypedDecisionClient
        {
            public List<TypedDecisionRequest> Requests { get; } = new List<TypedDecisionRequest>();

            public int InputTokens { get; set; } = 10;

            public int OutputTokens { get; set; } = 1;

            public string? FailReason { get; set; }

            public bool Throw { get; set; }

            public Task<TypedDecisionResult> DecideAsync(TypedDecisionRequest request, CancellationToken token)
            {
                Requests.Add(request);
                if (Throw) throw new InvalidOperationException("boom");
                if (FailReason != null) return Task.FromResult(new TypedDecisionResult { Available = false, UnavailableReason = FailReason });

                Dictionary<string, TypedAnswer> answers = new Dictionary<string, TypedAnswer>(StringComparer.Ordinal);
                foreach (string key in request.Questions.Keys) answers[key] = new TypedAnswer { Type = "noul", Noul = 0.5 };
                return Task.FromResult(new TypedDecisionResult
                {
                    Available = true,
                    Answers = answers,
                    Model = "jev-test",
                    InputTokens = InputTokens,
                    OutputTokens = OutputTokens
                });
            }
        }
    }
}
