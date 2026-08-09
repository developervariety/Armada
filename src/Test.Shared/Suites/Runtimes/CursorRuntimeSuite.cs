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
    /// Descriptors for <see cref="CursorRuntime"/> metadata and argument construction. Cases verify the
    /// default executable path, non-interactive text-output argument building, model flag inclusion, and
    /// that the resolved launch command references cursor-agent. Argument and command construction are
    /// exercised through an inspectable subclass.
    /// </summary>
    public sealed class CursorRuntimeSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Runtimes.CursorRuntime";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Cursor Runtime suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("executable_path_default_is_cursor_agent", "ExecutablePath Default Is CursorAgent", TestTags.Positive, () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                AssertEqual("cursor-agent", runtime.ExecutablePath);
            }));

            cases.Add(Case("build_arguments_uses_non_interactive_text_output", "BuildArguments Uses NonInteractive Text Output", TestTags.Positive, () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                AssertEqual("-p", args[0]);
                AssertEqual("test prompt", args[1]);
                AssertTrue(args.Contains("--force"));
                AssertTrue(args.Contains("--output-format"));
                AssertTrue(args.Contains("text"));
            }));

            cases.Add(Case("build_arguments_includes_model_when_supplied", "BuildArguments Includes Model When Supplied", TestTags.Positive, () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "gpt-5");
                int modelIndex = args.IndexOf("--model");
                AssertTrue(modelIndex >= 0);
                AssertEqual("gpt-5", args[modelIndex + 1]);
            }));

            cases.Add(Case("command_uses_cursor_agent", "Command Uses CursorAgent", TestTags.Positive, () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                string command = runtime.Command();
                AssertTrue(command.Contains("cursor-agent", StringComparison.OrdinalIgnoreCase), "Expected cursor-agent command");
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Cursor Runtime",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static InspectableCursorRuntime CreateRuntime()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InspectableCursorRuntime(logging);
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

        #endregion

        #region Private-Types

        /// <summary>
        /// Inspectable subclass exposing protected command and argument construction for assertions.
        /// </summary>
        private sealed class InspectableCursorRuntime : CursorRuntime
        {
            public InspectableCursorRuntime(LoggingModule logging) : base(logging)
            {
            }

            public string Command() => GetCommand();

            public List<string> Args(string prompt, string? model = null, string? finalMessageFilePath = null) =>
                BuildArguments(Path.GetTempPath(), prompt, model, finalMessageFilePath, null);
        }

        #endregion
    }
}
