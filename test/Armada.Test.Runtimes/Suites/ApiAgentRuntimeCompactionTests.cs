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
                List<RuntimeContextCompaction> records = new List<RuntimeContextCompaction>();
                runtime.OnContextCompacted += (processId, record) => records.Add(record);
                await runtime.EnsureConversationBoundsAsync(1, messages, "launch prompt", CancellationToken.None).ConfigureAwait(false);
                foreach (ChatMessage message in messages)
                    AssertFalse(IsCompacted(message), "and nothing is compacted");
                AssertEqual(0, records.Count, "and no compaction record is raised");
            });

            await RunTest("A compaction pass reports what it removed and keeps the system and launch prompts", async () =>
            {
                List<ChatMessage> messages = BuildConversation(60, 64 * 1024);
                messages[5].ToolName = "grep";
                int bytesBefore = MeasureBytes(messages);
                ApiAgentRuntime runtime = BuildRuntime();
                List<RuntimeContextCompaction> records = new List<RuntimeContextCompaction>();
                List<int> processIds = new List<int>();
                runtime.OnContextCompacted += (processId, record) =>
                {
                    processIds.Add(processId);
                    records.Add(record);
                };

                await runtime.EnsureConversationBoundsAsync(7, messages, "launch prompt", CancellationToken.None).ConfigureAwait(false);

                int compactedMessages = 0;
                foreach (ChatMessage message in messages)
                    if (IsCompacted(message)) compactedMessages++;

                AssertEqual(1, records.Count, "one ordinary pass raises one record");
                RuntimeContextCompaction ordinary = records[0];
                AssertEqual(7, processIds[0], "the record names the process that compacted");
                AssertFalse(ordinary.Forced, "the ordinary pass is not the ceiling pass");
                AssertEqual(compactedMessages, ordinary.CompactedResults, "the record counts every replaced result");
                AssertEqual(bytesBefore, (int)ordinary.BytesBefore, "the record carries the size before the pass");
                AssertEqual(MeasureBytes(messages), (int)ordinary.BytesAfter, "the record carries the size after the pass");
                AssertTrue(ordinary.DroppedBytes > (long)compactedMessages * 60 * 1024, "the record carries the removed bytes");
                AssertTrue(ordinary.DroppedBytesByTool.ContainsKey("grep"), "the removed bytes are split by tool");
                AssertEqual(ordinary.DroppedBytes, ordinary.DroppedBytesByTool["read"] + ordinary.DroppedBytesByTool["grep"],
                    "the per-tool split adds up to the total");
                AssertEqual("system prompt", messages[0].Content, "the system prompt survives whole");
                AssertEqual("launch prompt", messages[1].Content, "the launch prompt survives whole");
            });

            await RunTest("A failing compaction subscriber does not stop the run", async () =>
            {
                List<ChatMessage> messages = BuildConversation(60, 64 * 1024);
                ApiAgentRuntime runtime = BuildRuntime();
                runtime.OnContextCompacted += (processId, record) => throw new InvalidOperationException("subscriber failed");
                await runtime.EnsureConversationBoundsAsync(1, messages, "launch prompt", CancellationToken.None).ConfigureAwait(false);
                AssertTrue(IsCompacted(messages[3]), "the conversation is still compacted");
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
                // Failure lines make the ordinary pass keep every result, so only the ceiling pass can cut them.
                string failureBody = new string('x', 200 * 1024 - 80) + "\nFAILED: Assert.Equal expected 4 actual 5\nFailed: 1\n";
                foreach (ChatMessage message in messages)
                {
                    if (message.ToolName == "read") message.Content = failureBody;
                }
                AssertTrue(MeasureBytes(messages) > ApiAgentRuntime.MaximumConversationBytes,
                    "the conversation starts above the hard ceiling");
                ApiAgentRuntime runtime = BuildRuntime();
                List<RuntimeContextCompaction> records = new List<RuntimeContextCompaction>();
                runtime.OnContextCompacted += (processId, record) => records.Add(record);
                await runtime.EnsureConversationBoundsAsync(1, messages, "launch prompt", CancellationToken.None).ConfigureAwait(false);
                AssertTrue(MeasureBytes(messages) <= ApiAgentRuntime.MaximumConversationBytes,
                    "the ceiling override brought it back under the limit instead of throwing");
                RuntimeContextCompaction? ceiling = records.Find(record => record.Forced);
                AssertNotNull(ceiling, "the ceiling pass raises its own record");
                AssertTrue(ceiling!.CompactedResults > 0 && ceiling.DroppedBytes > 0, "the ceiling record carries what it removed");
                AssertEqual(MeasureBytes(messages), (int)ceiling.BytesAfter, "the ceiling record carries the final size");
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
