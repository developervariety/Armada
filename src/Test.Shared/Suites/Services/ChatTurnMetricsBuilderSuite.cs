namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="ChatTurnMetricsBuilder"/>. Verifies that completion tokens are estimated
    /// from the reply text when no real count is supplied (so a runtime's whole-context token estimate is
    /// never shown as the reply size), that a supplied real count is preferred, that tokens/sec is computed
    /// over the streaming window, and that streaming/rate are omitted when nothing streamed.
    /// </summary>
    public sealed class ChatTurnMetricsBuilderSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.ChatTurnMetricsBuilder";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the chat-turn metrics suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("estimates_completion_tokens_from_reply", "Estimates Completion Tokens From Reply", TestTags.Positive, () =>
            {
                // A ~350-char reply with no reported count must yield ~100 tokens (350 / 3.5), NOT a runtime
                // whole-context estimate (e.g. Mux's tens-of-thousands finalEstimatedTokens).
                string reply = new String('x', 350);
                CaptainChatMetrics metrics = ChatTurnMetricsBuilder.Build(23000, 20000, reply, null);
                AssertEqual(100, metrics.CompletionTokens);
                AssertTrue(metrics.CompletionTokens < 1000, "Estimated tokens must reflect the reply, not a huge context estimate");
            }));

            cases.Add(Case("prefers_reported_completion_tokens", "Prefers Reported Completion Tokens", TestTags.Positive, () =>
            {
                // When a real count is supplied (e.g. Claude output_tokens) it is used verbatim.
                CaptainChatMetrics metrics = ChatTurnMetricsBuilder.Build(5000, 1000, "irrelevant length here", 42);
                AssertEqual(42, metrics.CompletionTokens);
            }));

            cases.Add(Case("tokens_per_second_uses_streaming_window", "TokensPerSecond Uses Streaming Window", TestTags.Positive, () =>
            {
                // totalMs 5000, ttft 4000 => streamingMs 1000ms (1s); 50 reported tokens => 50 tokens/sec.
                CaptainChatMetrics metrics = ChatTurnMetricsBuilder.Build(5000, 4000, "n/a", 50);
                AssertEqual(1000.0, metrics.StreamingMs);
                AssertTrue(metrics.TokensPerSecond.HasValue && Math.Abs(metrics.TokensPerSecond.Value - 50.0) < 0.001,
                    "Expected 50 tokens/sec over the 1s streaming window");
            }));

            cases.Add(Case("omits_streaming_and_rate_without_first_token", "Omits Streaming And Rate Without First Token", TestTags.Negative, () =>
            {
                CaptainChatMetrics metrics = ChatTurnMetricsBuilder.Build(5000, null, "hi", null);
                AssertNull(metrics.StreamingMs);
                AssertNull(metrics.TokensPerSecond);
                AssertEqual(5000.0, metrics.TotalMs);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Chat Turn Metrics Builder",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        #endregion
    }
}
