namespace Armada.Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="RuntimeDetectionService"/>: PATH-based command probing,
    /// runtime enumeration, and per-runtime install hints. Positive cases cover successful
    /// detection and hint text; negative cases cover unavailable and empty command names.
    /// </summary>
    public sealed class RuntimeDetectionServiceSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Runtime Detection Service suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("is_command_available_git_returns_true", "IsCommandAvailable Git ReturnsTrue", TestTags.Positive, () =>
            {
                bool result = RuntimeDetectionService.IsCommandAvailable("git");
                AssertTrue(result);
            }));

            cases.Add(Case("is_command_available_nonexistent_returns_false", "IsCommandAvailable NonExistentCommand ReturnsFalse", TestTags.Negative, () =>
            {
                bool result = RuntimeDetectionService.IsCommandAvailable("armada_definitely_nonexistent_cmd_xyz");
                AssertFalse(result);
            }));

            cases.Add(Case("is_command_available_empty_returns_false", "IsCommandAvailable EmptyCommand ReturnsFalse", TestTags.Negative, () =>
            {
                // An empty file name causes Process.Start to throw; the service swallows it and reports unavailable.
                bool result = RuntimeDetectionService.IsCommandAvailable("");
                AssertFalse(result);
            }));

            cases.Add(Case("detect_all_runtimes_does_not_throw", "DetectAllRuntimes DoesNotThrow", TestTags.Positive, () =>
            {
                List<AgentRuntimeEnum> runtimes = RuntimeDetectionService.DetectAllRuntimes();
                AssertNotNull(runtimes);
            }));

            cases.Add(Case("detect_default_runtime_does_not_throw", "DetectDefaultRuntime DoesNotThrow", TestTags.Positive, () =>
            {
                AgentRuntimeEnum? runtime = RuntimeDetectionService.DetectDefaultRuntime();
                // Result is environment-dependent, no assertion on value
            }));

            cases.Add(Case("get_install_hint_claude_code_returns_npm_command", "GetInstallHint ClaudeCode ReturnsNpmCommand", TestTags.Positive, () =>
            {
                string hint = RuntimeDetectionService.GetInstallHint(AgentRuntimeEnum.ClaudeCode);
                AssertContains("npm install", hint);
                AssertContains("claude-code", hint);
            }));

            cases.Add(Case("get_install_hint_codex_returns_npm_command", "GetInstallHint Codex ReturnsNpmCommand", TestTags.Positive, () =>
            {
                string hint = RuntimeDetectionService.GetInstallHint(AgentRuntimeEnum.Codex);
                AssertContains("npm install", hint);
                AssertContains("codex", hint);
            }));

            cases.Add(Case("get_install_hint_custom_returns_generic_message", "GetInstallHint Custom ReturnsGenericMessage", TestTags.Positive, () =>
            {
                string hint = RuntimeDetectionService.GetInstallHint(AgentRuntimeEnum.Custom);
                AssertContains("documentation", hint);
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.RuntimeDetectionService",
                displayName: "Runtime Detection Service",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.RuntimeDetectionService",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.RuntimeDetectionService",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
