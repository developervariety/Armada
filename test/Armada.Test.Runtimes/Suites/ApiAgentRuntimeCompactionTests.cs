namespace Armada.Test.Runtimes.Suites
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using PolyPrompt.Models;
    using SyslogLogging;

    /// <summary>
    /// Deterministic API-endpoint conversation compaction: truncate older tool results to a bounded
    /// head, keep diagnostic and test-total lines, never remove a message, and stop the run only when
    /// the hard ceiling still cannot be met.
    /// </summary>
    public class ApiAgentRuntimeCompactionTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Api Agent Runtime Context Compaction";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Below the threshold nothing is compacted", async () =>
            {
                List<ChatMessage> messages = BuildConversation(6, 64);
                ApiAgentRuntime runtime = BuildRuntime();
                await runtime.EnsureConversationBoundsAsync(1, messages, "launch prompt", CancellationToken.None).ConfigureAwait(false);
                foreach (ChatMessage message in messages)
                    AssertFalse(IsCompacted(message), "and nothing is compacted");
            });

            await RunTest("An unspared listing truncates to a bounded head", async () =>
            {
                List<ChatMessage> messages = BuildConversation(60, 64 * 1024);
                string before = messages[3].Content!;
                ApiAgentRuntime runtime = BuildRuntime();
                await runtime.EnsureConversationBoundsAsync(1, messages, "launch prompt", CancellationToken.None).ConfigureAwait(false);
                AssertTrue(IsCompacted(messages[3]), "an older listing is compacted");
                AssertTrue(messages[3].Content!.Contains("Head kept:"), "the compacted body keeps a bounded head");
                AssertTrue(messages[3].Content!.Contains(before.Substring(0, ApiAgentRuntime.TruncatedHeadChars)),
                    "the head is the start of the original listing");
            });

            await RunTest("A test-failure result stays whole", async () =>
            {
                List<ChatMessage> messages = BuildConversation(60, 64 * 1024);
                int firstFailure = 5;
                int secondFailure = 7;
                int settled = 9;
                string failureBody = new string('x', 64 * 1024 - 80) + "\nFAILED: Assert.Equal expected 4 actual 5\nFailed: 1\n";
                messages[firstFailure].Content = failureBody;
                messages[secondFailure].Content = failureBody;
                string settledBefore = messages[settled].Content!;

                ApiAgentRuntime runtime = BuildRuntime();
                await runtime.EnsureConversationBoundsAsync(1, messages, "launch prompt", CancellationToken.None).ConfigureAwait(false);

                AssertEqual(failureBody, messages[firstFailure].Content, "the first failure is kept by the diagnostic rule");
                AssertEqual(failureBody, messages[secondFailure].Content, "the second failure is kept by the diagnostic rule");
                AssertTrue(IsCompacted(messages[settled]), "a settled listing still truncates");
                AssertTrue(messages[settled].Content!.Length < settledBefore.Length, "the truncated listing is smaller");
            });

            await RunTest("The ceiling override truncates diagnostic lines when the conversation is still over the limit", async () =>
            {
                List<ChatMessage> messages = BuildConversation(80, 200 * 1024);
                AssertTrue(MeasureBytes(messages) > ApiAgentRuntime.MaximumConversationBytes,
                    "the conversation starts above the hard ceiling");
                ApiAgentRuntime runtime = BuildRuntime();
                await runtime.EnsureConversationBoundsAsync(1, messages, "launch prompt", CancellationToken.None).ConfigureAwait(false);
                AssertTrue(MeasureBytes(messages) <= ApiAgentRuntime.MaximumConversationBytes,
                    "the ceiling override brought it back under the limit instead of throwing");
            });

            await RunTest("A conversation still over the ceiling with nothing left to compact stops the run", async () =>
            {
                List<ChatMessage> messages = new List<ChatMessage>();
                messages.Add(ChatMessage.System(new string('s', ApiAgentRuntime.MaximumConversationBytes + 1024)));
                messages.Add(ChatMessage.User("launch prompt"));
                for (int index = 0; index < 20; index++)
                {
                    messages.Add(ChatMessage.Assistant("calling a tool"));
                    messages.Add(ChatMessage.ToolResult("call_" + index, "read", "small"));
                }

                ApiAgentRuntime runtime = BuildRuntime();
                await AssertThrowsAsync<InvalidDataException>(
                    () => runtime.EnsureConversationBoundsAsync(1, messages, "launch prompt", CancellationToken.None),
                    "the hard ceiling still stops a conversation that cannot be compacted back").ConfigureAwait(false);
            });
        }

        #region Private-Methods

        private static ApiAgentRuntime BuildRuntime()
        {
            ModelEndpoint endpoint = new ModelEndpoint
            {
                Name = "compaction-test-endpoint",
                BaseUrl = "http://127.0.0.1:1/",
                Model = "test-model",
                Enabled = true
            };
            return new ApiAgentRuntime(endpoint, new LoggingModule());
        }

        private static bool IsCompacted(ChatMessage message)
        {
            return (message.Content ?? String.Empty).StartsWith(ApiAgentRuntime.CompactedToolResultMarker, StringComparison.Ordinal);
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
