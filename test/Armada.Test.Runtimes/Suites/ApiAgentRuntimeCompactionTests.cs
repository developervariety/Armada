namespace Armada.Test.Runtimes.Suites
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using PolyPrompt.Models;
    using SyslogLogging;

    /// <summary>
    /// The contract between the deterministic compaction and the <c>context_compaction</c> decision that
    /// layers on it. The deterministic pass replaces the content of every older tool result; the decision
    /// may only SPARE one of those, so every failure mode of the decision — absent, unavailable,
    /// throwing, or sparing so much that the conversation stays over the ceiling — must land back on the
    /// behaviour that shipped without it. A compaction that stops shrinking converts a long run into a
    /// lost run, which is the defect the deterministic pass was added to fix.
    /// </summary>
    public class ApiAgentRuntimeCompactionTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Api Agent Runtime Context Compaction";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("The candidate list names exactly what the deterministic pass would replace", () =>
            {
                // One predicate serves both, so a candidate the model is asked about and a message the pass
                // replaces can never disagree. Counted on the same conversation, twice.
                List<ChatMessage> forCandidates = BuildConversation(60, 64 * 1024);
                List<ContextCompactionCandidate> candidates =
                    ApiAgentRuntime.CollectCompactionCandidates(forCandidates, out List<int> indices);

                List<ChatMessage> forCompaction = BuildConversation(60, 64 * 1024);
                int compacted = ApiAgentRuntime.CompactConversation(forCompaction);

                AssertEqual(compacted, candidates.Count, "every message the pass replaces is offered as a candidate");
                AssertEqual(candidates.Count, indices.Count, "each candidate carries its message index");
                foreach (int index in indices)
                    AssertTrue(index >= 2, "the system prompt and the launch prompt are never candidates");
            });

            await RunTest("A candidate carries what was asked for and a bounded head of the answer", () =>
            {
                // Size alone cannot tell a settled listing from a measurement the captain still needs, so the
                // model is given the request and the head of the output. Bounded, because the point is to send less.
                List<ChatMessage> messages = BuildConversation(60, 64 * 1024);
                List<ContextCompactionCandidate> candidates =
                    ApiAgentRuntime.CollectCompactionCandidates(messages, out List<int> _);

                AssertTrue(candidates.Count > 0, "the conversation has candidates");
                ContextCompactionCandidate first = candidates[0];
                AssertEqual("read", first.ToolName, "the candidate names its tool");
                AssertContains("calling a tool", first.RequestSummary, "the request comes from the preceding assistant turn");
                AssertTrue(first.ResultHead.Length <= ApiAgentRuntime.CandidateHeadChars, "the head is bounded");
                AssertEqual(64 * 1024, first.ResultBytes, "the full size is reported even though the head is bounded");
                AssertTrue(first.TurnsAgo > 0, "the candidate says how far back it sits");
            });

            await RunTest("A spared result keeps its full output while the rest are compacted", async () =>
            {
                List<ChatMessage> messages = BuildConversation(60, 64 * 1024);
                List<ContextCompactionCandidate> candidates =
                    ApiAgentRuntime.CollectCompactionCandidates(messages, out List<int> indices);
                AssertTrue(candidates.Count >= 3, "the conversation has several candidates");

                int sparedMessageIndex = indices[1];
                string sparedBefore = messages[sparedMessageIndex].Content!;
                int otherMessageIndex = indices[2];

                ApiAgentRuntime runtime = BuildRuntime(SparingDecider(1));
                await runtime.EnsureConversationBoundsAsync(1, messages, "launch prompt", CancellationToken.None).ConfigureAwait(false);

                AssertEqual(sparedBefore, messages[sparedMessageIndex].Content, "the spared result keeps its full output verbatim");
                AssertTrue(IsCompacted(messages[otherMessageIndex]), "an unspared candidate is still compacted");
            });

            await RunTest("Below the deterministic threshold the decision is never consulted", async () =>
            {
                // The decision costs a provider call only on a conversation that was about to lose content.
                int calls = 0;
                List<ChatMessage> messages = BuildConversation(6, 64);
                ApiAgentRuntime runtime = BuildRuntime((input, token) =>
                {
                    calls++;
                    return Task.FromResult(ContextCompactionVerdict.SpareNone());
                });

                await runtime.EnsureConversationBoundsAsync(1, messages, "launch prompt", CancellationToken.None).ConfigureAwait(false);

                AssertEqual(0, calls, "a small conversation asks nothing");
                foreach (ChatMessage message in messages)
                    AssertFalse(IsCompacted(message), "and nothing is compacted");
            });

            await RunTest("An unavailable decision compacts exactly as the rule would", async () =>
            {
                // Unavailable returns the rule verdict, which spares nothing.
                List<ChatMessage> withDecision = BuildConversation(60, 64 * 1024);
                ApiAgentRuntime runtime = BuildRuntime((input, token) => Task.FromResult(ContextCompactionVerdict.SpareNone()));
                await runtime.EnsureConversationBoundsAsync(1, withDecision, "launch prompt", CancellationToken.None).ConfigureAwait(false);

                List<ChatMessage> ruleOnly = BuildConversation(60, 64 * 1024);
                ApiAgentRuntime.CompactConversation(ruleOnly);

                AssertEqual(CountCompacted(ruleOnly), CountCompacted(withDecision), "the same messages are compacted either way");
            });

            await RunTest("A decision that throws never reaches the caller and the rule still runs", async () =>
            {
                List<ChatMessage> messages = BuildConversation(60, 64 * 1024);
                ApiAgentRuntime runtime = BuildRuntime((input, token) => throw new InvalidOperationException("provider exploded"));

                await runtime.EnsureConversationBoundsAsync(1, messages, "launch prompt", CancellationToken.None).ConfigureAwait(false);

                AssertTrue(CountCompacted(messages) > 0, "a thrown decision degrades to the deterministic compaction");
            });

            await RunTest("Sparing that leaves the conversation over the ceiling compacts the spared results too", async () =>
            {
                // The decision can only retain more, so its sparing can leave a conversation the rule would
                // have brought under the ceiling above it. The ceiling wins: the run must not be lost.
                List<ChatMessage> messages = BuildConversation(80, 200 * 1024);
                AssertTrue(MeasureBytes(messages) > ApiAgentRuntime.MaximumConversationBytes,
                    "the conversation starts above the hard ceiling");

                ApiAgentRuntime runtime = BuildRuntime(SpareEverythingDecider());
                await runtime.EnsureConversationBoundsAsync(1, messages, "launch prompt", CancellationToken.None).ConfigureAwait(false);

                AssertTrue(MeasureBytes(messages) <= ApiAgentRuntime.MaximumConversationBytes,
                    "the ceiling override brought it back under the limit instead of throwing");
            });

            await RunTest("A conversation still over the ceiling with nothing left to compact stops the run", async () =>
            {
                // The ceiling is the last guard and it still throws, so a conversation that cannot be brought
                // back never silently runs on a context the provider will refuse.
                List<ChatMessage> messages = new List<ChatMessage>();
                messages.Add(ChatMessage.System(new string('s', ApiAgentRuntime.MaximumConversationBytes + 1024)));
                messages.Add(ChatMessage.User("launch prompt"));
                for (int index = 0; index < 20; index++)
                {
                    messages.Add(ChatMessage.Assistant("calling a tool"));
                    messages.Add(ChatMessage.ToolResult("call_" + index, "read", "small"));
                }

                ApiAgentRuntime runtime = BuildRuntime(SpareEverythingDecider());
                await AssertThrowsAsync<InvalidDataException>(
                    () => runtime.EnsureConversationBoundsAsync(1, messages, "launch prompt", CancellationToken.None),
                    "the hard ceiling still stops a conversation that cannot be compacted back").ConfigureAwait(false);
            });
        }

        #region Private-Methods

        private static ApiAgentRuntime BuildRuntime(Func<ContextCompactionDecisionInput, CancellationToken, Task<ContextCompactionVerdict>> decider)
        {
            ModelEndpoint endpoint = new ModelEndpoint
            {
                Name = "compaction-test-endpoint",
                BaseUrl = "http://127.0.0.1:1/",
                Model = "test-model",
                Enabled = true
            };
            ApiAgentRuntime runtime = new ApiAgentRuntime(endpoint, new LoggingModule());
            runtime.ContextCompactionDecider = decider;
            return runtime;
        }

        // Spares one candidate by position, the way a gated reading above the floor would.
        private static Func<ContextCompactionDecisionInput, CancellationToken, Task<ContextCompactionVerdict>> SparingDecider(int position)
        {
            return (input, token) => Task.FromResult(ContextCompactionVerdict.Sparing(new List<int> { position }));
        }

        // Spares every candidate it is offered: the worst case for the ceiling.
        private static Func<ContextCompactionDecisionInput, CancellationToken, Task<ContextCompactionVerdict>> SpareEverythingDecider()
        {
            return (input, token) =>
            {
                List<int> all = new List<int>();
                for (int index = 0; index < input.Candidates.Count; index++) all.Add(index);
                return Task.FromResult(ContextCompactionVerdict.Sparing(all));
            };
        }

        private static bool IsCompacted(ChatMessage message)
        {
            return (message.Content ?? String.Empty).StartsWith(ApiAgentRuntime.CompactedToolResultMarker, StringComparison.Ordinal);
        }

        private static int CountCompacted(List<ChatMessage> messages)
        {
            int count = 0;
            foreach (ChatMessage message in messages) if (IsCompacted(message)) count++;
            return count;
        }

        private static int MeasureBytes(List<ChatMessage> messages)
        {
            return System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(messages));
        }

        private static List<ChatMessage> BuildConversation(int exchanges, int toolResultBytes)
        {
            List<ChatMessage> messages = new List<ChatMessage>();
            messages.Add(ChatMessage.System("system prompt"));
            messages.Add(ChatMessage.User("launch prompt"));
            for (int index = 0; index < exchanges; index++)
            {
                messages.Add(ChatMessage.Assistant("calling a tool"));
                messages.Add(ChatMessage.ToolResult("call_" + index, "read", new string('x', toolResultBytes)));
            }

            return messages;
        }

        #endregion
    }
}
