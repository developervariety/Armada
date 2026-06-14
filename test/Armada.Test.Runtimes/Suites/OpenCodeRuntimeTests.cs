namespace Armada.Test.Runtimes.Suites
{
    using System.IO;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Tests for OpenCodeRuntime.
    /// </summary>
    public class OpenCodeRuntimeTests : TestSuite
    {
        /// <summary>
        /// Suite name.
        /// </summary>
        public override string Name => "OpenCode Runtime Tests";

        /// <summary>
        /// Inspectable subclass that exposes protected methods for testing without
        /// touching system paths or environment variables.
        /// </summary>
        private sealed class InspectableOpenCodeRuntime : OpenCodeRuntime
        {
            public InspectableOpenCodeRuntime(LoggingModule logging, OpenCodeServerSettings? settings = null)
                : base(logging, settings)
            {
            }

            /// <summary>
            /// Expose GetCommand() for testing.
            /// </summary>
            public string Command() => GetCommand();

            /// <summary>
            /// Expose BuildArguments() for testing.
            /// </summary>
            public List<string> Args(string prompt, string? model = null) =>
                BuildArguments(Path.GetTempPath(), prompt, model, null, null);

            /// <summary>
            /// Expose UsePromptStdin for testing.
            /// </summary>
            public bool StdinEnabled() => UsePromptStdin;

            /// <summary>
            /// Expose TransformOutputLine() so the TestEngineer can test JSON parsing
            /// without needing a running process.
            /// </summary>
            public string TransformLine(string line) => TransformOutputLine(line);
        }

        private InspectableOpenCodeRuntime CreateRuntime(OpenCodeServerSettings? settings = null)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InspectableOpenCodeRuntime(logging, settings);
        }

        /// <summary>
        /// Run all test cases.
        /// </summary>
        protected override async Task RunTestsAsync()
        {
            await RunTest("AgentRuntimeEnum_OpenCode_ParseSucceeds", () =>
            {
                bool parsed = Enum.TryParse("OpenCode", ignoreCase: true, out AgentRuntimeEnum runtime);
                AssertTrue(parsed, "Enum.TryParse must accept OpenCode for armada_create_captain runtime");
                AssertEqual(AgentRuntimeEnum.OpenCode, runtime);
            });

            await RunTest("ValidateReasoningEffort_Null_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, null);
                AssertNull(error, "Null reasoningEffort must be accepted (use OpenCode default)");
            });

            await RunTest("ValidateReasoningEffort_Low_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "low");
                AssertNull(error, "low must be accepted for OpenCode");
            });

            await RunTest("ValidateReasoningEffort_Medium_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "medium");
                AssertNull(error, "medium must be accepted for OpenCode");
            });

            await RunTest("ValidateReasoningEffort_High_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "high");
                AssertNull(error, "high must be accepted for OpenCode");
            });

            await RunTest("ValidateReasoningEffort_Xhigh_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "xhigh");
                AssertNull(error, "xhigh must be accepted for OpenCode");
            });

            await RunTest("ValidateReasoningEffort_Max_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "max");
                AssertNull(error, "max must be accepted for OpenCode");
            });

            await RunTest("ValidateReasoningEffort_InvalidValue_ReturnsError", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "ultra");
                AssertNotNull(error, "Unrecognised value must be rejected for OpenCode");
                AssertContains("Accepted values: low, medium, high, xhigh, max.", error!, "Error should list the supported values");
            });

            await RunTest("ValidateReasoningEffort_CaseInsensitive_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "HIGH");
                AssertNull(error, "Validation must be case-insensitive");
            });

            await RunTest("ValidateReasoningEffort_Whitespace_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.OpenCode, "   ");
                AssertNull(error, "Whitespace-only reasoningEffort must be treated as unset");
            });

            // --- Stdin / arg-limit guard ---

            await RunTest("UsePromptStdin Is True", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                AssertTrue(runtime.StdinEnabled(), "OpenCode runtime must use stdin to avoid Windows cmd.exe length limit");
            });

            await RunTest("BuildArguments_LongPrompt_PromptNotInArguments", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string longPrompt = new string('x', 16384);
                List<string> args = runtime.Args(longPrompt);
                foreach (string arg in args)
                {
                    AssertFalse(arg.Length > 1000, "No single argument should contain the long prompt; prompt must be sent via stdin");
                }
                AssertFalse(args.Contains(longPrompt), "Long prompt must not appear as a CLI argument");
            });

            // --- Argument structure ---

            await RunTest("BuildArguments_ContainsRunSubcommand", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                AssertTrue(args.Count > 0 && args[0] == "run", "First argument must be 'run'");
            });

            await RunTest("BuildArguments_ContainsFormatJson", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                int formatIndex = args.IndexOf("--format");
                AssertTrue(formatIndex >= 0, "--format must be present");
                AssertEqual("json", args[formatIndex + 1]);
            });

            await RunTest("BuildArguments_IncludesModelWhenSupplied", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "claude-sonnet-4-6");
                int modelIndex = args.IndexOf("-m");
                AssertTrue(modelIndex >= 0, "-m must be present when model supplied");
                AssertEqual("claude-sonnet-4-6", args[modelIndex + 1]);
            });

            await RunTest("BuildArguments_OmitsModelWhenNotSupplied", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                AssertFalse(args.Contains("-m"), "-m must be omitted when model is not supplied");
            });

            await RunTest("BuildArguments_ContainsAttachFlag", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                AssertTrue(args.Contains("--attach"), "--attach must always be present");
            });

            // --- Command resolution ---

            await RunTest("GetCommand_TestEnvVarOverride_UsesOverridePath", () =>
            {
                string fakePath = Path.Combine(Path.GetTempPath(), "fake-opencode.cmd");
                try
                {
                    Environment.SetEnvironmentVariable("ARMADA_TEST_OPENCODE", fakePath);
                    InspectableOpenCodeRuntime runtime = CreateRuntime();
                    string command = runtime.Command();
                    AssertEqual(fakePath, command, "ARMADA_TEST_OPENCODE must take priority over all other resolution");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("ARMADA_TEST_OPENCODE", null);
                }
            });

            // --- Shared connection settings (NO-DUPLICATION test) ---

            await RunTest("BuildArguments_AttachAndUsername_MatchSharedOpenCodeConnection", () =>
            {
                // Construct settings with non-default values.
                OpenCodeServerSettings settings = new OpenCodeServerSettings();
                settings.BaseUrl = "http://opencode.example.com:9999";
                settings.Username = "armada-user";

                // Build the same OpenCodeConnection the inference client uses.
                OpenCodeConnection connection = new OpenCodeConnection(settings);

                // Build the runtime over the same settings object.
                InspectableOpenCodeRuntime runtime = CreateRuntime(settings);
                List<string> args = runtime.Args("test prompt");

                // --attach must equal the connection's resolved BaseUrl.
                int attachIndex = args.IndexOf("--attach");
                AssertTrue(attachIndex >= 0, "--attach must be present");
                AssertEqual(
                    connection.ResolveBaseUrl(),
                    args[attachIndex + 1],
                    "--attach value must equal OpenCodeConnection.ResolveBaseUrl() -- same resolver, no duplicate");

                // -u must equal the connection's resolved Username.
                int userIndex = args.IndexOf("-u");
                AssertTrue(userIndex >= 0, "-u must be present for non-blank username");
                AssertEqual(
                    connection.ResolveUsername(),
                    args[userIndex + 1],
                    "-u value must equal OpenCodeConnection.ResolveUsername() -- same resolver, no duplicate");
            });

            // --- Variant / reasoning effort ---

            await RunTest("BuildArguments_OmitsVariantWhenNoReasoningEffort", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "some-model");
                AssertFalse(args.Contains("--variant"), "--variant must be absent when captain has no reasoning effort set");
            });

            // --- Factory ---

            await RunTest("Factory_Create_OpenCode_ReturnsOpenCodeRuntime", () =>
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                AgentRuntimeFactory factory = new AgentRuntimeFactory(logging);
                Armada.Runtimes.Interfaces.IAgentRuntime runtime = factory.Create(AgentRuntimeEnum.OpenCode);
                AssertNotNull(runtime);
                AssertEqual("OpenCode", runtime.Name);
            });

            await RunTest("Factory_Create_OpenCode_WithSettings_ReturnsOpenCodeRuntime", () =>
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                OpenCodeServerSettings settings = new OpenCodeServerSettings();
                settings.BaseUrl = "http://custom:1234";
                AgentRuntimeFactory factory = new AgentRuntimeFactory(logging, settings);
                Armada.Runtimes.Interfaces.IAgentRuntime runtime = factory.Create(AgentRuntimeEnum.OpenCode);
                AssertNotNull(runtime);
                AssertEqual("OpenCode", runtime.Name);
            });

            await Task.CompletedTask;
        }
    }
}
