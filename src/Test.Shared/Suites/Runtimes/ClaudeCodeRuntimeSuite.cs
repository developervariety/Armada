namespace Test.Shared.Suites.Runtimes
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Runtimes;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="ClaudeCodeRuntime"/> metadata and argument construction. Cases verify
    /// the runtime name and resume support, the default executable path and skip-permissions flag,
    /// null/empty executable-path rejection, model flag inclusion in built arguments, and invalid
    /// process-id liveness handling. Argument construction is exercised through an inspectable subclass.
    /// </summary>
    public sealed class ClaudeCodeRuntimeSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Runtimes.ClaudeCodeRuntime";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Claude Code Runtime suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("name_returns_claude_code", "Name Returns Claude Code", TestTags.Positive, () =>
            {
                ClaudeCodeRuntime runtime = CreateRuntime();
                AssertEqual("Claude Code", runtime.Name);
            }));

            cases.Add(Case("supports_resume_returns_true", "SupportsResume Returns True", TestTags.Positive, () =>
            {
                ClaudeCodeRuntime runtime = CreateRuntime();
                AssertTrue(runtime.SupportsResume);
            }));

            cases.Add(Case("executable_path_default_is_claude", "ExecutablePath Default Is Claude", TestTags.Positive, () =>
            {
                ClaudeCodeRuntime runtime = CreateRuntime();
                AssertEqual("claude", runtime.ExecutablePath);
            }));

            cases.Add(Case("executable_path_set_null_throws", "ExecutablePath Set Null Throws", TestTags.Negative, () =>
            {
                ClaudeCodeRuntime runtime = CreateRuntime();
                AssertThrows<ArgumentNullException>(() => runtime.ExecutablePath = null!);
            }));

            cases.Add(Case("executable_path_set_empty_throws", "ExecutablePath Set Empty Throws", TestTags.Negative, () =>
            {
                ClaudeCodeRuntime runtime = CreateRuntime();
                AssertThrows<ArgumentNullException>(() => runtime.ExecutablePath = "");
            }));

            cases.Add(Case("skip_permissions_default_is_true", "SkipPermissions Default Is True", TestTags.Positive, () =>
            {
                ClaudeCodeRuntime runtime = CreateRuntime();
                AssertTrue(runtime.SkipPermissions);
            }));

            cases.Add(Case("build_arguments_includes_model_when_supplied", "BuildArguments Includes Model When Supplied", TestTags.Positive, () =>
            {
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "sonnet");
                int modelIndex = args.IndexOf("--model");
                AssertTrue(modelIndex >= 0);
                AssertEqual("sonnet", args[modelIndex + 1]);
            }));

            cases.Add(Case("delivers_prompt_via_stdin_not_argument", "Delivers Prompt Via Stdin Not Argument", TestTags.Positive, () =>
            {
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                AssertTrue(runtime.PromptViaStdin);
                // The prompt is delivered on stdin, not as a CLI argument (avoids Windows cmd.exe
                // multi-line-argument truncation), so it must not appear in the argument list.
                List<string> args = runtime.Args("line one\nline two\nUser: real question", "sonnet");
                AssertFalse(args.Contains("line one\nline two\nUser: real question"));
            }));

            cases.Add(Case("build_arguments_omits_stream_json_by_default", "BuildArguments Omits StreamJson By Default", TestTags.Negative, () =>
            {
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("hi");
                AssertFalse(args.Contains("stream-json"));
                AssertFalse(args.Contains("--include-partial-messages"));
            }));

            cases.Add(Case("build_arguments_emits_stream_json_when_enabled", "BuildArguments Emits StreamJson When Enabled", TestTags.Positive, () =>
            {
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                runtime.StreamJsonOutput = true;
                List<string> args = runtime.Args("hi", "sonnet");
                int fmt = args.IndexOf("--output-format");
                AssertTrue(fmt >= 0);
                AssertEqual("stream-json", args[fmt + 1]);
                AssertTrue(args.Contains("--include-partial-messages"));
            }));

            cases.Add(CaseAsync("is_running_async_invalid_process_id_returns_false", "IsRunningAsync Invalid ProcessId Returns False", TestTags.Negative, async () =>
            {
                ClaudeCodeRuntime runtime = CreateRuntime();
                bool running = await runtime.IsRunningAsync(-1);
                AssertFalse(running);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Claude Code Runtime",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static InspectableClaudeCodeRuntime CreateRuntime()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InspectableClaudeCodeRuntime(logging);
        }

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

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion

        #region Private-Types

        /// <summary>
        /// Inspectable subclass exposing protected argument construction for assertions.
        /// </summary>
        private sealed class InspectableClaudeCodeRuntime : ClaudeCodeRuntime
        {
            public InspectableClaudeCodeRuntime(LoggingModule logging) : base(logging)
            {
            }

            public bool PromptViaStdin => UsePromptStdin;

            public List<string> Args(string prompt, string? model = null, string? finalMessageFilePath = null) =>
                BuildArguments(Path.GetTempPath(), prompt, model, finalMessageFilePath, null);
        }

        #endregion
    }
}
