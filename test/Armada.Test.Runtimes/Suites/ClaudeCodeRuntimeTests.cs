namespace Armada.Test.Runtimes.Suites
{
    using System.Diagnostics;
    using System.IO;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    public class ClaudeCodeRuntimeTests : TestSuite
    {
        public override string Name => "Claude Code Runtime Tests";

        private sealed class InspectableClaudeCodeRuntime : ClaudeCodeRuntime
        {
            public InspectableClaudeCodeRuntime(LoggingModule logging) : base(logging)
            {
            }

            public List<string> Args(string prompt, string? model = null, string? finalMessageFilePath = null, Captain? captain = null) =>
                BuildArguments(Path.GetTempPath(), prompt, model, finalMessageFilePath, captain);

            public List<string> ArgsIn(string workingDirectory, string prompt, string? model, Captain? captain) =>
                BuildArguments(workingDirectory, prompt, model, null, captain);

            public bool PromptViaStdin => UsePromptStdin;

            public Dictionary<string, string> LaunchVariables(Captain? captain = null)
            {
                Dictionary<string, string> variables = new Dictionary<string, string>();
                AddLaunchVariables(variables, null, captain);
                return variables;
            }

            public ProcessStartInfo StartInfoWithEnvironment(Captain? captain, string? existingThinkingBudget = null)
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                if (!String.IsNullOrEmpty(existingThinkingBudget))
                    startInfo.Environment["MAX_THINKING_TOKENS"] = existingThinkingBudget;

                ApplyEnvironment(startInfo, captain);
                return startInfo;
            }

            public void FeedUsage(int processId, string line) => HandleRawOutputLine(processId, line);
        }

        private InspectableClaudeCodeRuntime CreateRuntime()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InspectableClaudeCodeRuntime(logging);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Result PublishesExactUsage", () =>
            {
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                RuntimeTokenUsage? captured = null;
                runtime.OnTokenUsageReceived += (_, usage) => captured = usage;
                runtime.FeedUsage(7, "{\"type\":\"result\",\"usage\":{\"input_tokens\":2,\"output_tokens\":4,\"cache_read_input_tokens\":15273,\"cache_creation_input_tokens\":5528}}");
                AssertNotNull(captured);
                AssertTrue(captured!.InputTokens >= captured.CacheReadTokens, "Input includes the cache reads");
                AssertEqual(TokenUsageRuleEnum.SeparateInputBuckets, captured.UsageRule, "Claude usage is split into input buckets");
                AssertEqual(2L, captured.UncachedInputTokens, "Claude uncached input bucket");
                AssertEqual(15273L, captured.CacheReadTokens, "Claude cache-read input bucket");
                AssertEqual(5528L, captured.CacheWriteTokens, "Claude cache-write input bucket");
                AssertEqual(20803L, captured.InputTokens, "Claude input is the sum of the three buckets");
                AssertEqual(4L, captured.OutputTokens);
            });

            await RunTest("BuildArguments Includes SettingSources ProjectLocal", () =>
            {
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                int idx = args.IndexOf("--setting-sources");
                AssertTrue(idx >= 0, "--setting-sources flag missing");
                AssertEqual("project,local", args[idx + 1]);
            });

            await RunTest("BuildArguments Includes StrictMcpConfig", () =>
            {
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                AssertTrue(args.Contains("--strict-mcp-config"), "--strict-mcp-config flag missing");
            });

            await RunTest("BuildArguments Includes Isolation Flags When Model Supplied", () =>
            {
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "sonnet");
                int settingSourcesIndex = args.IndexOf("--setting-sources");
                AssertTrue(settingSourcesIndex >= 0, "--setting-sources flag missing");
                AssertEqual("project,local", args[settingSourcesIndex + 1]);
                AssertTrue(args.Contains("--strict-mcp-config"), "--strict-mcp-config flag missing");
            });

            await RunTest("Two missions on one captain launch with identical arguments that keep the system prompt cache-shared", () =>
            {
                // The prompt cache is shared only up to the first byte that differs, so a launch must
                // carry nothing mission-specific before the brief, and must ask the CLI to keep the
                // per-repository sections out of the system prompt.
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                Captain captain = new Captain { Name = "example-captain", Runtime = AgentRuntimeEnum.ClaudeCode, Model = "claude-example-1" };
                string firstDock = Path.Combine(Path.GetTempPath(), "docks", "ExampleVessel", "msn_first", "ExampleVessel");
                string secondDock = Path.Combine(Path.GetTempPath(), "docks", "OtherVessel", "msn_second", "OtherVessel");
                List<string> first = runtime.ArgsIn(firstDock, "Mission: first", captain.Model, captain);
                List<string> second = runtime.ArgsIn(secondDock, "Mission: second", captain.Model, captain);

                AssertEqual(String.Join("\n", first), String.Join("\n", second), "launch arguments must not vary by mission, dock or prompt");
                AssertTrue(first.Contains("--exclude-dynamic-system-prompt-sections"), "the dynamic system-prompt sections must be excluded");

                runtime.ExcludeDynamicSystemPromptSections = false;
                AssertFalse(runtime.ArgsIn(firstDock, "Mission: first", captain.Model, captain).Contains("--exclude-dynamic-system-prompt-sections"),
                    "the flag can be turned off for a CLI that does not accept it");
            });

            await RunTest("ValidateReasoningEffort_High_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.ClaudeCode, "high");
                AssertNull(error, "high must be accepted for ClaudeCode");
            });

            await RunTest("A Prompt Longer Than The Windows Command Line Limit Is Delivered On Stdin", () =>
            {
                // Windows caps a whole command line at 32,767 characters and cmd.exe, which runs the
                // npm .cmd shim, at 8,191; a mission brief routinely exceeds both.
                string prompt = "Role: You are an Armada worker agent. Mission: " + new string('x', 40000);
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args(prompt, "sonnet");
                AssertTrue(runtime.PromptViaStdin, "the prompt must reach Claude Code on stdin");
                AssertFalse(args.Any(arg => arg.Contains("Mission: ")), "no argument may carry the prompt");
                AssertTrue(String.Join(" ", args).Length < 8191, "the command line must stay inside the cmd.exe limit");
            });

            await RunTest("ValidateReasoningEffort_Xhigh_ReturnsError", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.ClaudeCode, "xhigh");
                AssertNotNull(error, "xhigh must be rejected for ClaudeCode");
                AssertContains("Accepted values: low, medium, high.", error!, "Error should list the supported values");
            });

            await RunTest("ValidateReasoningEffort_Max_ReturnsError", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.ClaudeCode, "max");
                AssertNotNull(error, "max must be rejected for ClaudeCode");
                AssertContains("Accepted values: low, medium, high.", error!, "Error should list the supported values");
            });

            await RunTest("PermissionPromptTool_RoutesToolPermissionsToArmadaAndRaisesTheMcpToolTimeout", () =>
            {
                InspectableClaudeCodeRuntime prompting = CreateRuntime();
                prompting.PermissionPromptTool = "mcp__armada__armada_cli_permission_prompt";
                prompting.PermissionPromptTimeoutSeconds = 90;
                List<string> args = prompting.Args("Look at the repository");
                int flag = args.IndexOf("--permission-prompt-tool");
                AssertTrue(flag >= 0, "The prompt tool flag is passed");
                AssertEqual("mcp__armada__armada_cli_permission_prompt", args[flag + 1], "The flag names the Armada tool");
                int mode = args.IndexOf("--permission-mode");
                AssertTrue(mode >= 0 && args[mode + 1] == "default", "Permissions are not bypassed");
                AssertFalse(args.Contains("--dangerously-skip-permissions"), "A prompting launch never skips permissions");
                AssertFalse(args.Contains("--tools"), "A prompting launch does not narrow the native tools: the owner decides each one");
                AssertEqual(((90 + ClaudeCodeRuntime.PermissionPromptTimeoutMarginSeconds) * 1000).ToString(), prompting.LaunchVariables()["MCP_TOOL_TIMEOUT"], "Claude Code waits longer than Armada's expiry");

                InspectableClaudeCodeRuntime mission = CreateRuntime();
                List<string> missionArgs = mission.Args("Do the work");
                AssertFalse(missionArgs.Contains("--permission-prompt-tool"), "A launch without the prompt tool, such as a mission, never carries the flag");
                AssertFalse(mission.LaunchVariables().ContainsKey("MCP_TOOL_TIMEOUT"), "A launch without the prompt tool leaves the MCP tool timeout alone");
            });

            await RunTest("ApplyEnvironment_HighReasoningEffort_SetsMaximumThinkingBudget", () =>
            {
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                Captain captain = new Captain("claude", AgentRuntimeEnum.ClaudeCode);
                captain.RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new CaptainOptions
                {
                    ReasoningEffort = "high"
                });

                ProcessStartInfo startInfo = runtime.StartInfoWithEnvironment(captain);

                AssertEqual("128000", startInfo.Environment["MAX_THINKING_TOKENS"], "high should map to Armada's maximum thinking budget");
            });

            await RunTest("ApplyEnvironment_Entrypoint_LabelsRequestsOnlyWhenConfigured", () =>
            {
                Captain captain = new Captain("claude", AgentRuntimeEnum.ClaudeCode);
                ArmadaSettings settings = new ArmadaSettings();

                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                runtime.Entrypoint = () => settings.ClaudeCodeEntrypoint;
                AssertFalse(runtime.StartInfoWithEnvironment(captain).Environment.ContainsKey("CLAUDE_CODE_ENTRYPOINT"),
                    "an unset label leaves the variable out, so the CLI keeps its own label");

                settings.ClaudeCodeEntrypoint = " sdk-cli ";
                AssertFalse(runtime.StartInfoWithEnvironment(captain).Environment.ContainsKey("CLAUDE_CODE_ENTRYPOINT"),
                    "an sdk- label is ignored, since it has no effect");

                settings.ClaudeCodeEntrypoint = " armada ";
                AssertEqual("armada", runtime.StartInfoWithEnvironment(captain).Environment["CLAUDE_CODE_ENTRYPOINT"],
                    "a configured label is sent trimmed");

                captain.RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new CaptainOptions { DisableExtendedThinking = true });
                AssertEqual("armada", runtime.StartInfoWithEnvironment(captain).Environment["CLAUDE_CODE_ENTRYPOINT"],
                    "the label survives a launch that disables extended thinking");
            });

            await RunTest("ApplyEnvironment_DisableExtendedThinking_RemovesThinkingBudget", () =>
            {
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                Captain captain = new Captain("claude", AgentRuntimeEnum.ClaudeCode);
                captain.RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new CaptainOptions
                {
                    ReasoningEffort = "high",
                    DisableExtendedThinking = true
                });

                ProcessStartInfo startInfo = runtime.StartInfoWithEnvironment(captain, "128000");

                AssertFalse(startInfo.Environment.ContainsKey("MAX_THINKING_TOKENS"), "disable flag should suppress inherited and computed thinking budgets");
            });

            if (OperatingSystem.IsWindows())
            {
                SkipTest("ProcessProviderError_UsesExactClaudeUsageProtocol", "the provider stub is a POSIX shell script");
            }
            else await RunTest("ProcessProviderError_UsesExactClaudeUsageProtocol", async () =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "armada-claude-provider-error-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    string executable = FinalReplyProcessTestHelper.CreateExecutable(directory, new[]
                    {
                        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"rate_limit_event HTTP 429 is narration\"}]}}",
                        "{\"type\":\"rate_limit_event\"}",
                        "{\"type\":\"result\",\"is_error\":true,\"result\":\"Claude AI usage limit reached|1712345678\"}"
                    }, 1);
                    InspectableClaudeCodeRuntime runtime = CreateRuntime();
                    runtime.ExecutablePath = executable;
                    RuntimeProviderError? captured = null;
                    int eventCount = 0;
                    runtime.OnProviderErrorReceived += (_, error) => { eventCount++; captured = error; };

                    await FinalReplyProcessTestHelper.StartAndWaitAsync(runtime, directory, Path.Combine(directory, "final.txt")).ConfigureAwait(false);

                    AssertEqual(1, eventCount, "Only the exact errored terminal usage protocol is typed evidence.");
                    AssertEqual("usage_limit_reached", captured!.ErrorType);
                    AssertNull(captured.HttpStatusCode);

                    string apiErrorExecutable = FinalReplyProcessTestHelper.CreateExecutable(directory, new[]
                    {
                        "{\"type\":\"result\",\"is_error\":true,\"result\":\"API Error: 500 {\\\"error\\\":{\\\"type\\\":\\\"authentication_error\\\"}}\"}"
                    }, 1);
                    runtime.ExecutablePath = apiErrorExecutable;
                    await FinalReplyProcessTestHelper.StartAndWaitAsync(runtime, directory, Path.Combine(directory, "api-final.txt")).ConfigureAwait(false);

                    AssertEqual(2, eventCount, "The exact API Error protocol contributes status and machine type evidence.");
                    AssertEqual((int?)500, captured!.HttpStatusCode);
                    AssertEqual("authentication_error", captured.ErrorType);
                }
                finally
                {
                    Directory.Delete(directory, true);
                }
            });

            if (OperatingSystem.IsWindows())
            {
                SkipTest("ProcessFinalReply_UsesSuccessfulTerminalResult", "the provider stub is a POSIX shell script");
            }
            else await RunTest("ProcessFinalReply_UsesSuccessfulTerminalResult", async () =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "armada-claude-final-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string finalPath = Path.Combine(directory, "final.txt");
                try
                {
                    string executable = FinalReplyProcessTestHelper.CreateExecutable(directory, new[]
                    {
                        "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Narration is not the final answer.\"},{\"type\":\"thinking\",\"thinking\":\"private reasoning\"},{\"type\":\"tool_use\",\"id\":\"tool-1\",\"name\":\"Read\",\"input\":{\"file_path\":\"sample.txt\"}}]}}",
                        "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tool-1\",\"content\":\"private tool output\"}]}}",
                        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"Claude final answer\"}"
                    }, 0);
                    InspectableClaudeCodeRuntime runtime = CreateRuntime();
                    runtime.ExecutablePath = executable;

                    int? exitCode = await FinalReplyProcessTestHelper.StartAndWaitAsync(runtime, directory, finalPath).ConfigureAwait(false);

                    AssertEqual((int?)0, exitCode, "A successful terminal result exits successfully.");
                    AssertEqual("Claude final answer", File.ReadAllText(finalPath), "The artifact contains only the terminal assistant answer.");

                    File.Delete(finalPath);
                    string failedExecutable = FinalReplyProcessTestHelper.CreateExecutable(directory, new[]
                    {
                        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"must not be saved\"}"
                    }, 7);
                    runtime.ExecutablePath = failedExecutable;
                    exitCode = await FinalReplyProcessTestHelper.StartAndWaitAsync(runtime, directory, finalPath).ConfigureAwait(false);
                    AssertEqual((int?)7, exitCode, "A provider success record does not override a failed process exit.");
                    AssertFalse(File.Exists(finalPath), "A nonzero process exit writes no final artifact.");
                }
                finally
                {
                    Directory.Delete(directory, true);
                }
            });
        }
    }
}
