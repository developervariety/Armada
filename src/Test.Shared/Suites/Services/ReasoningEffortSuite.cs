namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="ReasoningEffortTranslator"/> and the Mux effort flag in
    /// <see cref="MuxCommandBuilder"/>. Positive cases confirm each level maps to the right per-runtime
    /// control; negative cases confirm null/Off produce no control (or the runtime-appropriate absence).
    /// </summary>
    public sealed class ReasoningEffortSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the reasoning-effort suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // ---- Mux effort mapping ----
            cases.Add(Case("mux_effort_levels_map_to_mux_vocabulary", "Mux effort maps to off/minimal/low/medium/high", TestTags.Positive, () =>
            {
                AssertEqual("off", ReasoningEffortTranslator.ToMuxEffort(ReasoningEffortEnum.Off));
                AssertEqual("minimal", ReasoningEffortTranslator.ToMuxEffort(ReasoningEffortEnum.Minimal));
                AssertEqual("low", ReasoningEffortTranslator.ToMuxEffort(ReasoningEffortEnum.Low));
                AssertEqual("medium", ReasoningEffortTranslator.ToMuxEffort(ReasoningEffortEnum.Medium));
                AssertEqual("high", ReasoningEffortTranslator.ToMuxEffort(ReasoningEffortEnum.High));
            }));

            cases.Add(Case("mux_effort_null_returns_null", "Mux effort null returns null", TestTags.Negative, () =>
            {
                AssertNull(ReasoningEffortTranslator.ToMuxEffort(null));
            }));

            // ---- Codex reasoning_effort mapping ----
            cases.Add(Case("codex_effort_levels_map", "Codex reasoning effort maps minimal/low/medium/high", TestTags.Positive, () =>
            {
                AssertEqual("minimal", ReasoningEffortTranslator.ToCodexReasoningEffort(ReasoningEffortEnum.Minimal));
                AssertEqual("low", ReasoningEffortTranslator.ToCodexReasoningEffort(ReasoningEffortEnum.Low));
                AssertEqual("medium", ReasoningEffortTranslator.ToCodexReasoningEffort(ReasoningEffortEnum.Medium));
                AssertEqual("high", ReasoningEffortTranslator.ToCodexReasoningEffort(ReasoningEffortEnum.High));
            }));

            cases.Add(Case("codex_effort_off_and_null_omit", "Codex reasoning effort Off/null omit the override", TestTags.Negative, () =>
            {
                // Codex has no explicit "off", so Off (and null) must produce no override.
                AssertNull(ReasoningEffortTranslator.ToCodexReasoningEffort(ReasoningEffortEnum.Off));
                AssertNull(ReasoningEffortTranslator.ToCodexReasoningEffort(null));
            }));

            // ---- Claude thinking-token mapping ----
            cases.Add(Case("claude_effort_levels_map_to_budgets", "Claude effort maps to ascending thinking budgets", TestTags.Positive, () =>
            {
                AssertEqual(0, ReasoningEffortTranslator.ToClaudeThinkingTokens(ReasoningEffortEnum.Off)!.Value);
                AssertEqual(2048, ReasoningEffortTranslator.ToClaudeThinkingTokens(ReasoningEffortEnum.Minimal)!.Value);
                AssertEqual(4096, ReasoningEffortTranslator.ToClaudeThinkingTokens(ReasoningEffortEnum.Low)!.Value);
                AssertEqual(8192, ReasoningEffortTranslator.ToClaudeThinkingTokens(ReasoningEffortEnum.Medium)!.Value);
                AssertEqual(16384, ReasoningEffortTranslator.ToClaudeThinkingTokens(ReasoningEffortEnum.High)!.Value);
            }));

            cases.Add(Case("claude_effort_null_returns_null", "Claude effort null leaves env unset", TestTags.Negative, () =>
            {
                AssertNull(ReasoningEffortTranslator.ToClaudeThinkingTokens(null));
            }));

            // ---- Mux command builder wiring ----
            cases.Add(Case("mux_command_includes_effort_flag", "Mux print args include --effort when set", TestTags.Positive, () =>
            {
                List<string> args = MuxCommandBuilder.BuildPrintArguments("/tmp/wd", "hello", null, null, null, "low");
                int idx = args.IndexOf("--effort");
                AssertTrue(idx >= 0, "expected --effort in args");
                AssertEqual("low", args[idx + 1]);
            }));

            cases.Add(Case("mux_command_omits_effort_when_null", "Mux print args omit --effort when not set", TestTags.Negative, () =>
            {
                List<string> args = MuxCommandBuilder.BuildPrintArguments("/tmp/wd", "hello", null, null, null, null);
                AssertFalse(args.Contains("--effort"), "did not expect --effort in args");
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.ReasoningEffort",
                displayName: "Reasoning Effort Translator",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.ReasoningEffort",
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
