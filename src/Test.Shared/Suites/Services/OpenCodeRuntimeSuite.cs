namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using Armada.Runtimes;
    using SyslogLogging;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the OpenCode runtime's argument builder and provider-error rendering. Positive
    /// cases confirm the headless <c>opencode run</c> command shape; negative cases confirm optional
    /// flags are omitted, bad input is rejected rather than emitted, and provider errors render bounded.
    /// </summary>
    public sealed class OpenCodeRuntimeSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the OpenCode runtime suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // ---- Command builder ----
            cases.Add(Case("run_args_core_shape", "OpenCode run args start with run --format json; prompt goes to stdin", TestTags.Positive, () =>
            {
                List<string> args = OpenCodeCommandBuilder.BuildRunArguments("/tmp/wd", "do the thing", null, null, false, true);
                AssertEqual("run", args[0]);
                int fmt = args.IndexOf("--format");
                AssertTrue(fmt >= 0, "expected --format");
                AssertEqual("json", args[fmt + 1]);
                int dir = args.IndexOf("--dir");
                AssertTrue(dir >= 0, "expected --dir");
                AssertEqual("/tmp/wd", args[dir + 1]);
                // The prompt is delivered on stdin (OpenCodeRuntime.UsePromptStdin), not as a positional
                // argument, to avoid Windows cmd.exe multi-line-argument truncation.
                AssertFalse(args.Contains("do the thing"), "prompt must not be a CLI argument");
                AssertTrue(args.Contains("--auto"), "expected --auto when autoApprove");
            }));

            cases.Add(Case("run_args_model_variant_thinking", "OpenCode run args include model, variant, and thinking when set", TestTags.Positive, () =>
            {
                List<string> args = OpenCodeCommandBuilder.BuildRunArguments("/tmp/wd", "hi", "openai/gpt-5.2", "high", true, true);
                int m = args.IndexOf("--model");
                AssertTrue(m >= 0, "expected --model");
                AssertEqual("openai/gpt-5.2", args[m + 1]);
                int v = args.IndexOf("--variant");
                AssertTrue(v >= 0, "expected --variant");
                AssertEqual("high", args[v + 1]);
                AssertTrue(args.Contains("--thinking"), "expected --thinking");
            }));

            cases.Add(Case("run_args_omit_optionals", "OpenCode run args omit model/variant/thinking/auto when not set", TestTags.Negative, () =>
            {
                List<string> args = OpenCodeCommandBuilder.BuildRunArguments("/tmp/wd", "hi", null, null, false, false);
                AssertFalse(args.Contains("--model"), "did not expect --model");
                AssertFalse(args.Contains("--variant"), "did not expect --variant");
                AssertFalse(args.Contains("--thinking"), "did not expect --thinking");
                AssertFalse(args.Contains("--auto"), "did not expect --auto");
            }));

            cases.Add(Case("run_args_reject_blank", "OpenCode run args reject blank working dir / prompt", TestTags.Negative, () =>
            {
                AssertThrows<ArgumentNullException>(() => OpenCodeCommandBuilder.BuildRunArguments("", "hi", null, null, false, true));
                AssertThrows<ArgumentNullException>(() => OpenCodeCommandBuilder.BuildRunArguments("/tmp/wd", "", null, null, false, true));
            }));

            cases.Add(Case("api_error_is_safe_activity", "OpenCode API errors become bounded activity", TestTags.Negative, () =>
            {
                string line = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Test.Shared", "Fixtures", "opencode-api-error.jsonl")).Trim();
                TestOpenCodeRuntime runtime = new TestOpenCodeRuntime();
                string rendered = runtime.Transform(line);
                AssertTrue(rendered.Contains("opencode error", StringComparison.Ordinal), "expected named OpenCode error activity");
                AssertTrue(rendered.Contains("Local fixture provider rejected the request", StringComparison.Ordinal), "expected typed error message");
                AssertTrue(rendered.Contains("status 400", StringComparison.Ordinal), "expected typed status");
                AssertFalse(rendered.Contains("secret response body", StringComparison.Ordinal), "response body must not be rendered");
                AssertFalse(rendered.Contains("127.0.0.1", StringComparison.Ordinal), "metadata URL must not be rendered");
                AssertFalse(rendered.Contains("sessionID", StringComparison.Ordinal), "session metadata must not be rendered");
                AssertFalse(rendered.Contains("secret-value", StringComparison.Ordinal), "secret-bearing error code must be redacted");
                AssertTrue(ActivityRecords.IsProviderFailure(rendered), "chat and planning must classify the rendered error as failure");
                AssertFalse(ActivityRecords.IsProviderFailure("[ARMADA:ACTIVITY] tool read x (ok)"), "successful tool activity is not a provider failure");
            }));

            cases.Add(Case("provider_failure_is_one_shape", "Every runtime's provider failure record is recognised by the one predicate", TestTags.Negative, () =>
            {
                AssertTrue(ActivityRecords.IsProviderFailure("[ARMADA:ACTIVITY] codex error You've hit your usage limit. Try again at 3:05 PM."), "a Codex error event record is a provider failure");
                AssertTrue(ActivityRecords.IsProviderFailure("[ARMADA:ACTIVITY] gemini error quota exceeded"), "a Gemini error event record is a provider failure");
                AssertTrue(ActivityRecords.IsProviderFailure("[ARMADA:ACTIVITY] api error inference call failed: overloaded"), "an API-endpoint failure record is a provider failure");
                AssertTrue(ActivityRecords.IsProviderFailure("[ARMADA:ACTIVITY] claude error (1 turns)"), "an errored Claude result is a provider failure");
                AssertEqual("codex error rate limited", ActivityRecords.ProviderFailureText("[ARMADA:ACTIVITY] codex error rate limited"), "the failure text drops only the activity marker");
                AssertFalse(ActivityRecords.IsProviderFailure("[ARMADA:ACTIVITY] tool error x (ok)"), "a tool record is never a provider failure");
                AssertFalse(ActivityRecords.IsProviderFailure("[ARMADA:ACTIVITY] codex item error reconnecting"), "a non-fatal Codex item error is not a provider failure");
                AssertFalse(ActivityRecords.IsProviderFailure("[ARMADA:ACTIVITY] claude result success (3 turns)"), "a successful result is not a provider failure");
                AssertFalse(ActivityRecords.IsProviderFailure("[ARMADA:ACTIVITY] codex errored"), "the word must be exactly error");
                AssertFalse(ActivityRecords.IsProviderFailure("codex error in prose"), "a line without the activity marker is not a record");
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.OpenCodeRuntime",
                displayName: "OpenCode Runtime",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.OpenCodeRuntime",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        private sealed class TestOpenCodeRuntime : OpenCodeRuntime
        {
            public TestOpenCodeRuntime()
                : base(new LoggingModule())
            {
            }

            public string Transform(string line)
            {
                return TransformOutputLine(line);
            }
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "README.md"))
                    && Directory.Exists(Path.Combine(current.FullName, "src")))
                    return current.FullName;
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("Could not find the Armada repository root.");
        }

        #endregion
    }
}
