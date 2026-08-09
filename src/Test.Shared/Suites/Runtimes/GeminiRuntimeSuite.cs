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
    /// Descriptors for <see cref="GeminiRuntime"/> metadata and argument construction. Cases verify the
    /// default approval mode, prompt-and-approval-mode argument building, model flag inclusion, and
    /// Windows command wrapper resolution. Argument and command construction are exercised through an
    /// inspectable subclass.
    /// </summary>
    public sealed class GeminiRuntimeSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Runtimes.GeminiRuntime";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Gemini Runtime suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("approval_mode_default_is_yolo", "ApprovalMode Default Is Yolo", TestTags.Positive, () =>
            {
                InspectableGeminiRuntime runtime = CreateRuntime();
                AssertEqual("yolo", runtime.ApprovalMode);
            }));

            cases.Add(Case("build_arguments_uses_prompt_and_approval_mode", "BuildArguments Uses Prompt And ApprovalMode", TestTags.Positive, () =>
            {
                InspectableGeminiRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                AssertEqual("-p", args[0]);
                AssertEqual("test prompt", args[1]);
                AssertTrue(args.Contains("--approval-mode"));
                AssertTrue(args.Contains("yolo"));
            }));

            cases.Add(Case("build_arguments_includes_model_when_supplied", "BuildArguments Includes Model When Supplied", TestTags.Positive, () =>
            {
                InspectableGeminiRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "gemini-2.5-pro");
                int modelIndex = args.IndexOf("--model");
                AssertTrue(modelIndex >= 0);
                AssertEqual("gemini-2.5-pro", args[modelIndex + 1]);
            }));

            cases.Add(Case("windows_command_resolves_cmd_wrapper", "Windows Command Resolves Cmd Wrapper", TestTags.Positive, () =>
            {
                InspectableGeminiRuntime runtime = CreateRuntime();
                string command = runtime.Command();

                if (OperatingSystem.IsWindows())
                    AssertTrue(command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || command.Equals("gemini", StringComparison.OrdinalIgnoreCase), "Expected gemini command to resolve to .cmd or gemini");
                else
                    AssertEqual("gemini", command);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Gemini Runtime",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static InspectableGeminiRuntime CreateRuntime()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InspectableGeminiRuntime(logging);
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
        private sealed class InspectableGeminiRuntime : GeminiRuntime
        {
            public InspectableGeminiRuntime(LoggingModule logging) : base(logging)
            {
            }

            public string Command() => GetCommand();

            public List<string> Args(string prompt, string? model = null, string? finalMessageFilePath = null) =>
                BuildArguments(Path.GetTempPath(), prompt, model, finalMessageFilePath, null);
        }

        #endregion
    }
}
