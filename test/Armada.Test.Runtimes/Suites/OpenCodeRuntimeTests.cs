namespace Armada.Test.Runtimes.Suites
{
    using System.IO;
    using Armada.Core.Enums;
    using Armada.Core.Models;
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
            /// Expose BuildArguments() for testing. An optional captain lets the
            /// TestEngineer exercise the per-captain reasoning-effort (--variant) path.
            /// </summary>
            public List<string> Args(string prompt, string? model = null, Captain? captain = null) =>
                BuildArguments(Path.GetTempPath(), prompt, model, null, captain);

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

            await RunTest("BuildArguments_IncludesDangerouslySkipPermissions", () =>
            {
                // Runtime override for the dock external_directory permission: the flag
                // auto-approves non-denied permissions, beating opencode's broken Windows
                // path-glob matcher and the non-interactive run-mode permission preset.
                // It must be present for every standard run.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                AssertTrue(args.Contains("--dangerously-skip-permissions"), "--dangerously-skip-permissions must be present so dock external_directory access is not auto-rejected");
            });

            await RunTest("BuildArguments_DangerouslySkipPermissions_AppearsExactlyOnce", () =>
            {
                // Idempotency guard: the override must be added exactly once. A duplicate flag
                // (e.g. a future edit that re-adds it on another code path) would still satisfy
                // a Contains check, so count occurrences explicitly.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "claude-sonnet-4-6");
                int count = 0;
                foreach (string arg in args)
                {
                    if (arg == "--dangerously-skip-permissions") count++;
                }
                AssertEqual(1, count, "--dangerously-skip-permissions must appear exactly once, never duplicated");
            });

            await RunTest("BuildArguments_DangerouslySkipPermissions_FollowsFormatJson", () =>
            {
                // Placement guard: the documented position is right after the `--format json`
                // flags. opencode treats --dangerously-skip-permissions as a `run` flag, so it
                // must sit after the run subcommand and after the --format/json pair, never
                // before the `run` subcommand where opencode would not parse it.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "claude-sonnet-4-6");
                int runIndex = args.IndexOf("run");
                int formatValueIndex = args.IndexOf("json");
                int skipIndex = args.IndexOf("--dangerously-skip-permissions");
                AssertTrue(skipIndex > runIndex, "--dangerously-skip-permissions must come after the 'run' subcommand");
                AssertTrue(skipIndex > formatValueIndex, "--dangerously-skip-permissions must come after the --format json pair");
            });

            await RunTest("BuildArguments_DangerouslySkipPermissions_PresentWithModelVariantAndAgent", () =>
            {
                // Combination guard: the existing presence test exercised the bare no-model path.
                // The flag must also survive the fullest arg set -- model (-m), reasoning effort
                // (--variant) and the resolved --agent all present -- so a future reorder around
                // those flags cannot drop the permission override.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                Captain captain = new Captain("opencode", AgentRuntimeEnum.OpenCode);
                captain.RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new CaptainOptions
                {
                    ReasoningEffort = "high"
                });

                List<string> args = runtime.Args("test prompt", "claude-sonnet-4-6", captain);

                AssertTrue(args.Contains("--dangerously-skip-permissions"), "--dangerously-skip-permissions must remain present alongside -m, --variant and --agent");
                AssertTrue(args.Contains("-m"), "-m must remain present in the full arg set");
                AssertTrue(args.Contains("--variant"), "--variant must remain present in the full arg set");
                AssertTrue(args.Contains("--agent"), "--agent must remain present in the full arg set");
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
                    "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"Hello world\"}}"
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
                    "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"Hello \"}}",
                    "{\"type\":\"tool_call\"}",
                    "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"world\"}}"
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
                    "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"[ARMADA:RESULT] COMPLETE\"}}",
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
                    "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"\"}}",
                    "{\"type\":\"text\"}"
                };
                bool found = runtime.ExtractAssistantResult(lines, out string text);
                AssertFalse(found, "Assistant events with no text content must not count as output");
                AssertEqual(String.Empty, text, "No real content means empty extracted text");
            });

            await RunTest("TryExtractAssistantResult_UnknownTypeWithTextPart_IsNotCounted", () =>
            {
                // Only top-level text events with a nested text part count as assistant output.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> lines = new List<string>
                {
                    "{\"type\":\"some-future-event\",\"part\":{\"type\":\"text\",\"text\":\"not surfaced\"}}"
                };
                bool found = runtime.ExtractAssistantResult(lines, out string text);
                AssertFalse(found, "An unknown-type event with a text part must not count as assistant output");
                AssertEqual(String.Empty, text, "No recognized text event means empty extracted text");
            });

            // --- TransformOutputLine event-stream parsing ---

            await RunTest("TransformOutputLine_AssistantEvent_ReturnsInnerContent", () =>
            {
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string result = runtime.TransformLine("{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"[ARMADA:PROGRESS] 50\"}}");
                AssertEqual("[ARMADA:PROGRESS] 50", result, "Assistant event must reduce to its inner text so markers stay ^-anchored");
            });

            await RunTest("TransformOutputLine_StepStartEvent_ReturnsEmpty", () =>
            {
                // A content-free event (step_start) carries no assistant text and should not
                // leak raw JSON into the mission log.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string line = "{\"type\":\"step_start\"}";
                string result = runtime.TransformLine(line);
                AssertEqual(String.Empty, result, "Content-free structured events must be suppressed");
            });

            await RunTest("TransformOutputLine_StepFinishAndToolEvents_AreSuppressed", () =>
            {
                // The Worker added step_finish / step-finish / tool* to the recognized-noise set
                // so the live opencode 1.17.7 stream's trailing step_finish and tool events do not
                // leak raw JSON into the mission log. Only step_start was previously pinned; this
                // exercises the other recognized-noise branches so they stay suppressed (empty).
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                AssertEqual(
                    String.Empty,
                    runtime.TransformLine("{\"type\":\"step_finish\",\"tokens\":{\"input\":10}}"),
                    "step_finish events must be suppressed so token-usage JSON does not leak");
                AssertEqual(
                    String.Empty,
                    runtime.TransformLine("{\"type\":\"step-finish\"}"),
                    "hyphenated step-finish must also be suppressed");
                AssertEqual(
                    String.Empty,
                    runtime.TransformLine("{\"type\":\"tool_call\",\"name\":\"bash\"}"),
                    "tool_call events must be suppressed");
                AssertEqual(
                    String.Empty,
                    runtime.TransformLine("{\"type\":\"tool\"}"),
                    "any tool-prefixed event must be suppressed");
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

                // Build the same OpenCodeConnection the captain runtime uses.
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

            await RunTest("BuildArguments_ReasoningEffortSet_ForwardsVariantWithValue", () =>
            {
                // Positive variant path: the existing suite only covered omission. A captain
                // whose runtime options carry a reasoning effort must forward it as
                // --variant <value> so the standalone run honors the requested tier.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                Captain captain = new Captain("opencode", AgentRuntimeEnum.OpenCode);
                captain.RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new CaptainOptions
                {
                    ReasoningEffort = "high"
                });

                List<string> args = runtime.Args("test prompt", "claude-sonnet-4-6", captain);

                int variantIndex = args.IndexOf("--variant");
                AssertTrue(variantIndex >= 0, "--variant must be present when the captain set a reasoning effort");
                AssertEqual("high", args[variantIndex + 1], "--variant value must equal the captain's reasoning effort");
                // The variant flag must not displace the core standalone-run flags.
                AssertTrue(args.Contains("-m"), "-m must remain present alongside --variant");
                int formatIndex = args.IndexOf("--format");
                AssertTrue(formatIndex >= 0, "--format must remain present alongside --variant");
                AssertEqual("json", args[formatIndex + 1], "--format value must remain json alongside --variant");
            });

            await RunTest("BuildArguments_ReasoningEffortPadded_VariantValueNormalizedAndForwarded", () =>
            {
                // End-to-end normalization guard: a padded reasoning effort must reach the CLI
                // trimmed, never with stray whitespace that opencode would reject.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                Captain captain = new Captain("opencode", AgentRuntimeEnum.OpenCode);
                captain.RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new CaptainOptions
                {
                    ReasoningEffort = "  xhigh  "
                });

                List<string> args = runtime.Args("test prompt", "claude-sonnet-4-6", captain);

                int variantIndex = args.IndexOf("--variant");
                AssertTrue(variantIndex >= 0, "--variant must be present for a padded reasoning effort");
                AssertEqual("xhigh", args[variantIndex + 1], "--variant value must be trimmed before reaching the CLI");
            });

            await RunTest("BuildArguments_BlankReasoningEffortOnCaptain_OmitsVariant", () =>
            {
                // A captain whose reasoning effort normalizes away (whitespace-only) must NOT
                // forward an empty --variant flag; the runtime omits it entirely.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                Captain captain = new Captain("opencode", AgentRuntimeEnum.OpenCode);
                captain.RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new CaptainOptions
                {
                    ReasoningEffort = "   "
                });

                List<string> args = runtime.Args("test prompt", "claude-sonnet-4-6", captain);
                AssertFalse(args.Contains("--variant"), "--variant must be omitted when the captain's reasoning effort is blank/normalized away");
            });

            // --- Agent flag (captain resolver default) ---

            await RunTest("BuildArguments_DefaultConnection_EmitsAgentBuild", () =>
            {
                // With default (blank) settings the shared OpenCodeConnection resolves the agent
                // to "build"; the standalone run must still forward --agent <resolved> so the
                // captain runs under the configured coding agent.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "claude-sonnet-4-6");
                int agentIndex = args.IndexOf("--agent");
                AssertTrue(agentIndex >= 0, "--agent must be present using the shared connection default");
                AssertEqual("build", args[agentIndex + 1], "--agent must equal OpenCodeConnection.ResolveAgent() default (build)");
            });

            await RunTest("OpenCodeServerSettings_Defaults_SplitInferenceSummaryFromCaptainBuild", () =>
            {
                // Acceptance guard: the captain coding-agent default is "build" while the existing
                // inference/code-index Agent default MUST remain "summary" (untouched by this fix).
                // These two defaults are independent and must not collapse onto one value.
                OpenCodeServerSettings settings = new OpenCodeServerSettings();
                AssertEqual("summary", settings.Agent, "Inference Agent default must stay 'summary' (code-index summarization path)");
                AssertEqual("build", settings.CaptainAgent, "Captain coding-agent default must be 'build'");
            });

            await RunTest("OpenCodeServerSettings_NullSetters_FallBackToOwnDefaults", () =>
            {
                // The CaptainAgent setter mirrors the existing Agent setter's null-coalescing
                // style: a null assignment reverts to the property's own default, never the other
                // property's default. Proves the two agent settings stay independent.
                OpenCodeServerSettings settings = new OpenCodeServerSettings();
                settings.CaptainAgent = null!;
                AssertEqual("build", settings.CaptainAgent, "Null CaptainAgent must fall back to 'build'");
                settings.Agent = null!;
                AssertEqual("summary", settings.Agent, "Null Agent must fall back to 'summary', not 'build'");
            });

            await RunTest("OpenCodeConnection_ResolveAgent_ConfiguredCaptainAgent_IsForwarded", () =>
            {
                // ResolveAgent must be settings-driven (configurable), reading CaptainAgent rather
                // than hardcoding "build". A non-blank CaptainAgent flows through verbatim.
                OpenCodeServerSettings settings = new OpenCodeServerSettings();
                settings.CaptainAgent = "custom-coder";
                OpenCodeConnection connection = new OpenCodeConnection(settings);
                AssertEqual("custom-coder", connection.ResolveAgent(), "Configured CaptainAgent must be returned verbatim");
            });

            await RunTest("OpenCodeConnection_ResolveAgent_BlankCaptainAgent_NeverFallsBackToSummary", () =>
            {
                // Regression guard for the captain path: even with the inference Agent explicitly
                // set to "summary", a blank captain agent must resolve to "build" -- never to the
                // inference "summary" default. The captain coding path must not run under summary.
                OpenCodeServerSettings settings = new OpenCodeServerSettings();
                settings.Agent = "summary";
                settings.CaptainAgent = "   ";
                OpenCodeConnection connection = new OpenCodeConnection(settings);
                AssertEqual("build", connection.ResolveAgent(), "Blank CaptainAgent must resolve to 'build', not the inference 'summary' default");
            });

            // --- Code-index decoupling (captain path must not touch the daemon serve path) ---

            await RunTest("BuildArguments_CaptainPath_OmitsDaemonServeFlags", () =>
            {
                // Regression guard for the standalone-run change: the captain invocation must
                // stay decoupled from the OpenCodeServer code-index daemon. None of the daemon
                // serve/attach flags may bleed into the captain args.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "claude-sonnet-4-6");
                AssertTrue(args.Count > 0 && args[0] == "run", "Captain path must use the 'run' subcommand, not 'serve'");
                AssertFalse(args.Contains("serve"), "Captain path must NOT emit the daemon 'serve' subcommand");
                AssertFalse(args.Contains("--port"), "Captain path must NOT emit the daemon --port flag");
                AssertFalse(args.Contains("--hostname"), "Captain path must NOT emit the daemon --hostname flag");
                AssertFalse(args.Contains("--attach"), "Captain path must NOT attach to the daemon");
            });

            await RunTest("BuildArguments_CaptainPath_DoesNotReferenceConfiguredDaemonBaseUrl", () =>
            {
                // Even when a distinctive daemon BaseUrl is configured (used by the code-index
                // inference client), the standalone captain run must never reference it. This
                // proves the captain output path no longer depends on the attached daemon.
                OpenCodeServerSettings settings = new OpenCodeServerSettings();
                settings.BaseUrl = "http://daemon.example.invalid:65000";
                InspectableOpenCodeRuntime runtime = CreateRuntime(settings);
                List<string> args = runtime.Args("test prompt", "claude-sonnet-4-6");
                foreach (string arg in args)
                {
                    AssertFalse(
                        arg.Contains("daemon.example.invalid"),
                        "Captain args must not reference the configured code-index daemon BaseUrl");
                }
            });

            await RunTest("OpenCodeConnection_ResolveBaseUrl_StillResolvesDaemonBaseUrl", () =>
            {
                // BaseUrl resolution remains intact: a configured daemon BaseUrl still resolves
                // trimmed with no trailing slash, and a blank one still falls back to the
                // documented localhost default.
                OpenCodeServerSettings configured = new OpenCodeServerSettings();
                configured.BaseUrl = "http://daemon.example.invalid:65000/";
                OpenCodeConnection withUrl = new OpenCodeConnection(configured);
                AssertEqual("http://daemon.example.invalid:65000", withUrl.ResolveBaseUrl(), "Configured daemon BaseUrl must resolve trimmed for the inference client");

                OpenCodeConnection blank = new OpenCodeConnection(new OpenCodeServerSettings());
                AssertEqual("http://127.0.0.1:4096", blank.ResolveBaseUrl(), "Blank settings must fall back to the localhost daemon default");
            });

            // --- Classifier / production guard parity ---

            await RunTest("TransformOutputLine_And_TryExtractAssistantResult_AgreeOnClassification", () =>
            {
                // The production empty-run guard runs through TransformOutputLine (it flips the
                // saw-assistant-output flag that HandleProcessExited checks). TryExtractAssistantResult
                // is a separate, heavily-tested classifier that is NOT wired into the run path.
                // This parity test guards against the two drifting: for every line,
                // TransformOutputLine must extract inner content (return != line) exactly when
                // TryExtractAssistantResult would count that line as assistant output.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> samples = new List<string>
                {
                    "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"[ARMADA:RESULT] COMPLETE\"}}",
                    "{\"type\":\"step_start\"}",
                    "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"\"}}",
                    "{\"type\":\"text\"}",
                    "{\"type\":\"some-future-event\",\"part\":{\"type\":\"text\",\"text\":\"not surfaced\"}}",
                    "not-json progress 50%",
                    ""
                };

                foreach (string line in samples)
                {
                    string transformed = runtime.TransformLine(line);
                    bool transformExtracted = transformed != line && transformed != String.Empty;
                    bool classifierExtracted = runtime.ExtractAssistantResult(new List<string> { line }, out _);
                    AssertEqual(
                        classifierExtracted,
                        transformExtracted,
                        "TransformOutputLine and TryExtractAssistantResult must classify '" + line + "' identically (wired guard vs tested classifier must not drift)");
                }
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

            // --- Real opencode 1.17.7 nested event-schema coverage ---

            await RunTest("RealJsonl_TextEvent_TransformLineReturnsPartText", () =>
            {
                // Verbatim live sample from opencode 1.17.7: assistant text is nested inside
                // part.text, not in a top-level content field.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string line = "{\"type\":\"text\",\"timestamp\":1,\"part\":{\"id\":\"p1\",\"messageID\":\"m1\",\"sessionID\":\"s1\",\"type\":\"text\",\"text\":\"I'll read the mission instructions...\"}}";
                string result = runtime.TransformLine(line);
                AssertEqual("I'll read the mission instructions...", result, "TransformLine must extract part.text from the real opencode 1.17.7 text event");
            });

            await RunTest("RealJsonl_StepStartTextStepFinish_ExtractsPartText", () =>
            {
                // Full verbatim stream: step_start -> text (with nested part) -> step_finish.
                // ExtractAssistantResult must return true and join the part.text.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> lines = new List<string>
                {
                    "{\"type\":\"step_start\"}",
                    "{\"type\":\"text\",\"timestamp\":1,\"part\":{\"id\":\"p1\",\"messageID\":\"m1\",\"sessionID\":\"s1\",\"type\":\"text\",\"text\":\"I'll read the mission instructions...\"}}",
                    "{\"type\":\"step_finish\",\"tokens\":{\"input\":10,\"output\":45}}"
                };
                bool found = runtime.ExtractAssistantResult(lines, out string text);
                AssertTrue(found, "ExtractAssistantResult must return true when stream contains a real opencode 1.17.7 text event");
                AssertEqual("I'll read the mission instructions...", text, "Extracted text must equal the inner part.text value");
            });

            await RunTest("RealJsonl_StepOnlyStream_NoContentExtracted", () =>
            {
                // step_start + step_finish with no text event: no assistant content produced.
                // This pins the empty-run warning path: _SawAssistantOutput stays false and
                // HandleProcessExited logs a warning instead of silently succeeding.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> lines = new List<string>
                {
                    "{\"type\":\"step_start\"}",
                    "{\"type\":\"step_finish\",\"tokens\":{\"input\":10,\"output\":45}}"
                };
                bool found = runtime.ExtractAssistantResult(lines, out string text);
                AssertFalse(found, "step-only stream must not yield assistant content");
                AssertEqual(String.Empty, text, "step-only stream must produce empty extracted text");
            });

            await RunTest("RealJsonl_StepFinishEvent_TransformLineReturnsEmpty", () =>
            {
                // Verbatim step_finish sample: must be suppressed from the log (empty string),
                // not leaked as raw JSON that would confuse the ProgressParser.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string line = "{\"type\":\"step_finish\",\"tokens\":{\"input\":10,\"output\":45}}";
                string result = runtime.TransformLine(line);
                AssertEqual(String.Empty, result, "step_finish must be suppressed from the mission log");
            });

            await RunTest("RealJsonl_StepStartEvent_TransformLineReturnsEmpty", () =>
            {
                // A step_start event (content-free by design) must be suppressed from the log,
                // not fall through as raw JSON. Pins the new suppression path in TransformOutputLine.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string line = "{\"type\":\"step_start\"}";
                string result = runtime.TransformLine(line);
                AssertEqual(String.Empty, result, "step_start must be suppressed from the mission log, not returned as raw JSON");
            });

            await RunTest("RealJsonl_ArmadaResultMarker_InPartText_DetectableAfterTransform", () =>
            {
                // [ARMADA:RESULT] COMPLETE embedded in part.text must survive the transform
                // unchanged so the admiral's ^-anchored regex still fires on the output line.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string line = "{\"type\":\"text\",\"timestamp\":1,\"part\":{\"id\":\"p1\",\"messageID\":\"m1\",\"sessionID\":\"s1\",\"type\":\"text\",\"text\":\"[ARMADA:RESULT] COMPLETE\"}}";
                string result = runtime.TransformLine(line);
                AssertEqual("[ARMADA:RESULT] COMPLETE", result, "Transformed output must equal the marker text exactly");
                AssertTrue(result.StartsWith("[ARMADA:RESULT]"), "Transformed output must start with [ARMADA:RESULT] so ^-anchored detection fires");
            });

            await RunTest("RealJsonl_ArmadaVerdictMarker_InPartText_DetectableAfterTransform", () =>
            {
                // [ARMADA:VERDICT] PASS embedded in part.text must survive the transform so the
                // admiral's verdict detection still fires on the output line.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string line = "{\"type\":\"text\",\"timestamp\":1,\"part\":{\"id\":\"p1\",\"messageID\":\"m1\",\"sessionID\":\"s1\",\"type\":\"text\",\"text\":\"[ARMADA:VERDICT] PASS\"}}";
                string result = runtime.TransformLine(line);
                AssertEqual("[ARMADA:VERDICT] PASS", result, "Transformed output must equal the verdict marker text exactly");
                AssertTrue(result.StartsWith("[ARMADA:VERDICT]"), "Transformed output must start with [ARMADA:VERDICT] so ^-anchored detection fires");
            });

            await RunTest("RealJsonl_TextEvent_NoRawJsonLeakedInTransformedOutput", () =>
            {
                // The transformed output must contain only the extracted text -- none of the
                // raw JSON wrapper (part, sessionID, messageID) may leak into the log line.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string line = "{\"type\":\"text\",\"timestamp\":1,\"part\":{\"id\":\"p1\",\"messageID\":\"m1\",\"sessionID\":\"s1\",\"type\":\"text\",\"text\":\"Hello from opencode\"}}";
                string result = runtime.TransformLine(line);
                AssertEqual("Hello from opencode", result, "Only the extracted text must appear in the transformed output");
                AssertFalse(result.Contains("\"part\""), "Transformed output must not contain the raw JSON 'part' key");
                AssertFalse(result.Contains("sessionID"), "Transformed output must not contain the raw JSON 'sessionID' key");
            });

            await RunTest("RealJsonl_OldAssistantContentDto_NotExtracted", () =>
            {
                // Regression guard: the pre-M1 {type,content} shape must not be extracted under
                // the new nested schema. A top-level content field carries no part.text, so the
                // new IsAssistantContentEvent predicate must ignore it.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string line = "{\"type\":\"assistant\",\"content\":\"x\"}";
                bool found = runtime.ExtractAssistantResult(new List<string> { line }, out string text);
                AssertFalse(found, "Old {type,content} DTO shape must not be extracted under the new nested schema");
                AssertEqual(String.Empty, text, "Old DTO shape must produce empty extracted text");
            });

            await RunTest("RealJsonl_EmptyPartText_NotCounted", () =>
            {
                // A text event whose part.text is empty carries no real output and must not flip
                // the saw-content flag, matching the behavior for empty content in the old DTO.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string line = "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"\"}}";
                bool found = runtime.ExtractAssistantResult(new List<string> { line }, out string text);
                AssertFalse(found, "A text event with empty part.text must not count as assistant output");
                AssertEqual(String.Empty, text, "Empty part.text must produce empty extracted text");
            });

            await RunTest("RealJsonl_MultipleTextEvents_ConcatenateInOrder", () =>
            {
                // Multiple text events across two steps must be joined in stream order, exactly
                // as the old multi-assistant-event concatenation test required.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> lines = new List<string>
                {
                    "{\"type\":\"step_start\"}",
                    "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"First \"}}",
                    "{\"type\":\"step_finish\",\"tokens\":{\"input\":5,\"output\":10}}",
                    "{\"type\":\"step_start\"}",
                    "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"second\"}}",
                    "{\"type\":\"step_finish\",\"tokens\":{\"input\":5,\"output\":10}}"
                };
                bool found = runtime.ExtractAssistantResult(lines, out string text);
                AssertTrue(found, "Multiple text events must yield content");
                AssertEqual("First second", text, "Multiple text events must be concatenated in stream order");
            });

            await RunTest("RealJsonl_StreamWithTextEvent_TransformLineExtractsContentAndSuppressesSteps", () =>
            {
                // Simulates a complete opencode 1.17.7 run: step_start -> text -> step_finish.
                // TransformLine must return empty for the step events and the inner text for the
                // text event, pinning that _SawAssistantOutput would be true after the text call
                // while step events leave it unset.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string stepStartResult = runtime.TransformLine("{\"type\":\"step_start\"}");
                AssertEqual(String.Empty, stepStartResult, "step_start TransformLine must return empty");
                string textResult = runtime.TransformLine("{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"I'll read the mission instructions...\"}}");
                AssertEqual("I'll read the mission instructions...", textResult, "text event TransformLine must return inner part.text");
                string stepFinishResult = runtime.TransformLine("{\"type\":\"step_finish\",\"tokens\":{\"input\":10,\"output\":45}}");
                AssertEqual(String.Empty, stepFinishResult, "step_finish TransformLine must return empty");
            });

            await RunTest("RealJsonl_NoiseAndBlankLinesAroundTextEvent_PartTextExtracted", () =>
            {
                // New-schema analogue of the old TryExtractAssistantResult_NoiseAndBlankLines
                // case: blank lines and non-JSON progress noise must be skipped (the defensive
                // IsNullOrEmpty + try/catch paths) while a real nested text event still surfaces
                // its inner part.text. Proves the [ARMADA:*] marker riding inside part.text is
                // not lost when interleaved with stream noise.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                List<string> lines = new List<string>
                {
                    "",
                    "not-json progress bar 50%",
                    "{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"[ARMADA:RESULT] COMPLETE\"}}",
                    ""
                };
                bool found = runtime.ExtractAssistantResult(lines, out string text);
                AssertTrue(found, "Noise and blank lines must not prevent part.text extraction under the nested schema");
                AssertEqual("[ARMADA:RESULT] COMPLETE", text, "Only the inner part.text is collected; surrounding noise is dropped");
            });

            await RunTest("RealJsonl_TextEventMissingPartText_NotCounted", () =>
            {
                // A text event whose part object omits the text field entirely (null, not just
                // empty string) carries no real output and must not flip the saw-content flag.
                // This is schema-agnostic -- it yields no content under either the old top-level
                // content DTO or the new nested part.text shape -- so it pins the missing-field
                // boundary distinctly from the empty-string case.
                InspectableOpenCodeRuntime runtime = CreateRuntime();
                string line = "{\"type\":\"text\",\"part\":{\"id\":\"p1\",\"type\":\"text\"}}";
                bool found = runtime.ExtractAssistantResult(new List<string> { line }, out string text);
                AssertFalse(found, "A text event with no part.text field must not count as assistant output");
                AssertEqual(String.Empty, text, "Missing part.text must produce empty extracted text");
            });

            await Task.CompletedTask;
        }
    }
}
