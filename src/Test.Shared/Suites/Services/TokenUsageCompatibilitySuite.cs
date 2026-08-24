namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Verifies that legacy mission token events are included by the current summary source and that
    /// records written to both the event stream and token_usage table are counted once.
    /// </summary>
    public sealed class TokenUsageCompatibilitySuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.TokenUsageCompatibility";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the token-usage compatibility suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();
            cases.Add(Case("legacy_events_are_added_and_current_records_are_not_duplicated", () =>
            {
                JsonSerializerOptions jsonOptions = new JsonSerializerOptions();
                TokenUsageRecord current = Record("msn_current", "claudecode", "claude-sonnet-4", 100, 25, 10);
                ArmadaEvent duplicate = Event("msn_current", "claudecode", "claude-sonnet-4", 100, 25, 10, jsonOptions);
                ArmadaEvent legacy = Event("msn_legacy", "codex", "gpt-5", 40, 60, 0, jsonOptions);

                List<TokenUsageRecord> merged = TokenUsageCompatibility.MergeLegacyEvents(
                    new List<TokenUsageRecord> { current },
                    new List<ArmadaEvent> { duplicate, legacy },
                    jsonOptions);

                AssertEqual(2, merged.Count, "The duplicate is suppressed and the legacy record is added");
                AssertEqual("msn_current", merged[0].SourceId, "The current record remains");
                AssertEqual("msn_legacy", merged[1].SourceId, "The legacy record is included");
                AssertEqual(40L, merged[1].InputTokens, "Legacy input is retained");
                AssertEqual(100L, merged[1].TotalTokens, "Legacy total is input plus output when provider total is absent");
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Token Usage Compatibility",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TokenUsageRecord Record(string sourceId, string runtime, string model, long input, long output, long cached)
        {
            return new TokenUsageRecord
            {
                SourceId = sourceId,
                Source = "mission",
                Runtime = runtime,
                Model = model,
                InputTokens = input,
                OutputTokens = output,
                CachedTokens = cached,
                TotalTokens = input + output
            };
        }

        private static ArmadaEvent Event(
            string missionId,
            string runtime,
            string model,
            long input,
            long output,
            long cached,
            JsonSerializerOptions jsonOptions)
        {
            return new ArmadaEvent("mission.token_usage", "legacy token usage")
            {
                MissionId = missionId,
                EntityId = missionId,
                Payload = JsonSerializer.Serialize(new RuntimeTokenUsage
                {
                    Runtime = runtime,
                    Model = model,
                    InputTokens = input,
                    OutputTokens = output,
                    CacheReadTokens = cached
                }, jsonOptions)
            };
        }

        private static TestCaseDescriptor Case(string caseId, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: "Legacy events merge without duplicates",
                executeAsync: _ =>
                {
                    body();
                    return System.Threading.Tasks.Task.CompletedTask;
                },
                tags: new List<string> { TestTags.Positive });
        }

        #endregion
    }
}
