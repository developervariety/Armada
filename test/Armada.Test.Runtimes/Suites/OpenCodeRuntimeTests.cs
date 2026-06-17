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

            /// <summary>
            /// Expose TryExtractAssistantResult() so the assistant-result classifier can
            /// be tested directly without a running process.
            /// </summary>
            public bool ExtractAssistantResult(IReadOnlyList<string> lines, out string text) =>
                TryExtractAssistantResult(lines, out text);
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

            await RunTest("BuildArguments_OmitsAttachAndCredentialFlags", () =>
            {
                // Standalone `opencode run` drops the daemon-attach flags: --attach (and
                // the -p/-u Basic-auth credentials that only served the attached server).
                // Attaching returned only a step_start event on opencode 1.17.7; standalone
                // run returns the assistant result.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "claude-sonnet-4-6");
                AssertFalse(args.Contains("--attach"), "--attach must NOT be present for standalone run");
                AssertFalse(args.Contains("-p"), "-p must NOT be present for standalone run");
                AssertFalse(args.Contains("-u"), "-u must NOT be present for standalone run");
            });

            await RunTest("BuildArguments_StandaloneRunRetainsCoreFlags", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "claude-sonnet-4-6");
                AssertTrue(args.Count > 0 && args[0] == "run", "First argument must remain 'run'");
                int formatIndex = args.IndexOf("--format");
                AssertTrue(formatIndex >= 0, "--format must remain present");
                AssertEqual("json", args[formatIndex + 1], "--format value must remain json");
                int modelIndex = args.IndexOf("-m");
                AssertTrue(modelIndex >= 0, "-m must remain present when model supplied");
                AssertEqual("claude-sonnet-4-6", args[modelIndex + 1], "-m value must equal supplied model");
            });

            await RunTest("TryExtractAssistantResult_StepStartThenAssistant_ReturnsTextAndTrue", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> lines = new List<string>
                {
                    "{\"type\":\"step_start\"}",
                    "{\"type\":\"assistant\",\"content\":\"Hello world\"}"
                };
                bool found = runtime.ExtractAssistantResult(lines, out string text);
                AssertTrue(found, "A step_start-then-assistant stream must yield assistant content");
                AssertEqual("Hello world", text, "Extracted text must equal the assistant content");
            });

            await RunTest("TryExtractAssistantResult_StepStartOnly_ReturnsFalseAndEmpty", () =>
            {
                // This is the exact failure mode the standalone-run change fixes: an attached
                // daemon returned ONLY a step_start event and never streamed assistant content.
                // The classifier must report false (no real output) so the empty run is not
                // mis-read as WorkProduced.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> lines = new List<string> { "{\"type\":\"step_start\"}" };
                bool found = runtime.ExtractAssistantResult(lines, out string text);
                AssertFalse(found, "A step_start-only stream must NOT be treated as assistant output");
                AssertEqual(String.Empty, text, "No assistant content means empty extracted text");
            });

            await RunTest("TryExtractAssistantResult_EmptyList_ReturnsFalseAndEmpty", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                bool found = runtime.ExtractAssistantResult(new List<string>(), out string text);
                AssertFalse(found, "An empty stream yields no assistant content");
                AssertEqual(String.Empty, text, "Empty stream must produce empty text");
            });

            await RunTest("TryExtractAssistantResult_NullList_ReturnsFalseAndEmpty", () =>
            {
                // Defensive: a null line list (e.g. a process that produced no stdout at all)
                // must not throw; it is treated as an empty, content-free stream.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                bool found = runtime.ExtractAssistantResult(null!, out string text);
                AssertFalse(found, "A null stream must not be treated as assistant output");
                AssertEqual(String.Empty, text, "Null stream must produce empty text");
            });

            await RunTest("TryExtractAssistantResult_MultipleAssistantEvents_ConcatenatesInOrder", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> lines = new List<string>
                {
                    "{\"type\":\"step_start\"}",
                    "{\"type\":\"assistant\",\"content\":\"Hello \"}",
                    "{\"type\":\"tool_call\"}",
                    "{\"type\":\"assistant\",\"content\":\"world\"}"
                };
                bool found = runtime.ExtractAssistantResult(lines, out string text);
                AssertTrue(found, "Multiple assistant events must yield content");
                AssertEqual("Hello world", text, "Assistant content must be concatenated in stream order");
            });

            await RunTest("TryExtractAssistantResult_NoiseAndBlankLines_IgnoredButAssistantExtracted", () =>
            {
                // Non-JSON progress noise and blank lines must be skipped without aborting the
                // scan; the embedded [ARMADA:*] marker rides inside the assistant content and
                // must survive so the admiral can Contains-detect it.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> lines = new List<string>
                {
                    "",
                    "not-json progress bar 50%",
                    "{\"type\":\"assistant\",\"content\":\"[ARMADA:RESULT] COMPLETE\"}",
                    ""
                };
                bool found = runtime.ExtractAssistantResult(lines, out string text);
                AssertTrue(found, "Noise lines must not prevent assistant content extraction");
                AssertEqual("[ARMADA:RESULT] COMPLETE", text, "Only assistant content is collected; noise is dropped");
            });

            await RunTest("TryExtractAssistantResult_AssistantWithEmptyContent_NotCounted", () =>
            {
                // An assistant event with empty/missing content carries no real output and must
                // not flip the saw-content flag to true.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> lines = new List<string>
                {
                    "{\"type\":\"assistant\",\"content\":\"\"}",
                    "{\"type\":\"assistant\"}"
                };
                bool found = runtime.ExtractAssistantResult(lines, out string text);
                AssertFalse(found, "Assistant events with no text content must not count as output");
                AssertEqual(String.Empty, text, "No real content means empty extracted text");
            });

            await RunTest("TryExtractAssistantResult_UnknownTypeWithContent_IsTolerated", () =>
            {
                // The classifier is intentionally tolerant: any event carrying non-empty content
                // is treated as assistant output, even when the type string is unrecognized.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> lines = new List<string>
                {
                    "{\"type\":\"some-future-event\",\"content\":\"surfaced anyway\"}"
                };
                bool found = runtime.ExtractAssistantResult(lines, out string text);
                AssertTrue(found, "An unknown-type event with content must still be surfaced");
                AssertEqual("surfaced anyway", text, "Content of an unknown-type event must be extracted");
            });

            // --- TransformOutputLine event-stream parsing ---

            await RunTest("TransformOutputLine_AssistantEvent_ReturnsInnerContent", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string result = runtime.TransformLine("{\"type\":\"assistant\",\"content\":\"[ARMADA:PROGRESS] 50\"}");
                AssertEqual("[ARMADA:PROGRESS] 50", result, "Assistant event must reduce to its inner text so markers stay ^-anchored");
            });

            await RunTest("TransformOutputLine_StepStartEvent_ReturnsLineUnchanged", () =>
            {
                // A content-free event (step_start) carries no assistant text; the original line
                // falls through unchanged rather than being silently dropped.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string line = "{\"type\":\"step_start\"}";
                string result = runtime.TransformLine(line);
                AssertEqual(line, result, "Content-free events must fall through unchanged");
            });

            await RunTest("TransformOutputLine_NonJsonLine_ReturnsLineUnchanged", () =>
            {
                // Non-parseable noise (progress bars, debug output) must not be dropped from the
                // mission log; it falls through verbatim.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string line = "building... 80%";
                string result = runtime.TransformLine(line);
                AssertEqual(line, result, "Non-JSON lines must pass through unchanged");
            });

            await RunTest("TransformOutputLine_EmptyLine_ReturnsLineUnchanged", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string result = runtime.TransformLine(String.Empty);
                AssertEqual(String.Empty, result, "Empty input must be returned unchanged");
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

            await RunTest("BuildArguments_Agent_MatchesSharedOpenCodeConnection", () =>
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

                // The daemon-attach flags are gone; --agent stays and must use the same resolver.
                AssertFalse(args.Contains("--attach"), "--attach must be dropped for standalone run");
                AssertFalse(args.Contains("-u"), "-u must be dropped for standalone run");

                string resolvedAgent = connection.ResolveAgent();
                if (!String.IsNullOrWhiteSpace(resolvedAgent))
                {
                    int agentIndex = args.IndexOf("--agent");
                    AssertTrue(agentIndex >= 0, "--agent must be present for non-blank agent");
                    AssertEqual(
                        resolvedAgent,
                        args[agentIndex + 1],
                        "--agent value must equal OpenCodeConnection.ResolveAgent() -- same resolver, no duplicate");
                }
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
