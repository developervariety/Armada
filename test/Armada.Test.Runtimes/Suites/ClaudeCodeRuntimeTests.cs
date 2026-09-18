namespace Armada.Test.Runtimes.Suites
{
    using System.Diagnostics;
    using System.IO;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
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
            await RunTest("Name Returns Claude Code", () =>
            {
                ClaudeCodeRuntime runtime = CreateRuntime();
                AssertEqual("Claude Code", runtime.Name);
            });

            await RunTest("SupportsResume Returns True", () =>
            {
                ClaudeCodeRuntime runtime = CreateRuntime();
                AssertTrue(runtime.SupportsResume);
            });

            await RunTest("ExecutablePath Default Is Claude", () =>
            {
                ClaudeCodeRuntime runtime = CreateRuntime();
                AssertEqual("claude", runtime.ExecutablePath);
            });

            await RunTest("ExecutablePath Set Null Throws", () =>
            {
                ClaudeCodeRuntime runtime = CreateRuntime();
                AssertThrows<ArgumentNullException>(() => runtime.ExecutablePath = null!);
            });

            await RunTest("ExecutablePath Set Empty Throws", () =>
            {
                ClaudeCodeRuntime runtime = CreateRuntime();
                AssertThrows<ArgumentNullException>(() => runtime.ExecutablePath = "");
            });

            await RunTest("SkipPermissions Default Is True", () =>
            {
                ClaudeCodeRuntime runtime = CreateRuntime();
                AssertTrue(runtime.SkipPermissions);
            });

            await RunTest("BuildArguments Includes Model When Supplied", () =>
            {
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "sonnet");
                int modelIndex = args.IndexOf("--model");
                AssertTrue(modelIndex >= 0);
                AssertEqual("sonnet", args[modelIndex + 1]);
                AssertTrue(args.Contains("stream-json"));
            });

            await RunTest("Result PublishesExactUsage", () =>
            {
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                RuntimeTokenUsage? captured = null;
                runtime.OnTokenUsageReceived += (_, usage) => captured = usage;
                runtime.FeedUsage(7, "{\"type\":\"result\",\"usage\":{\"input_tokens\":2,\"output_tokens\":4,\"cache_read_input_tokens\":15273,\"cache_creation_input_tokens\":5528}}");
                AssertNotNull(captured);
                AssertEqual(2L, captured!.InputTokens);
                AssertEqual(4L, captured.OutputTokens);
                AssertEqual(15273L, captured.CacheReadTokens);
                AssertEqual(5528L, captured.CacheWriteTokens);
            });

            await RunTest("The context-compaction plugin and its hooks switch are passed only when the plugin is shipped", () =>
            {
                // Without the switch Claude Code loads the plugin, registers it and validates its hooks, and never
                // runs them - so the two must travel together, and neither may point at a plugin that is not there.
                string previous = HarnessPlugins.Root;
                string root = Path.Combine(Path.GetTempPath(), "armada-plugins-" + Guid.NewGuid().ToString("N"));
                string plugin = Path.Combine(root, "claude-code", "armada-context-compaction");
                try
                {
                    Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
                    File.WriteAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{}");
                    HarnessPlugins.Root = root;

                    List<string> args = CreateRuntime().Args("do the work");
                    int at = args.IndexOf("--plugin-dir");
                    AssertTrue(at >= 0 && at + 1 < args.Count, "the shipped plugin is passed explicitly");
                    AssertEqual(plugin, args[at + 1]);
                    AssertEqual("1", CreateRuntime().StartInfoWithEnvironment(null).Environment[HarnessPlugins.ClaudeCodeFunctionHooksVariable],
                        "with the switch that makes its hooks run");

                    // A plan for a remote runner carries neither: the path exists only here, and a runner refuses
                    // a launch variable it does not know.
                    InspectableClaudeCodeRuntime plan = CreateRuntime();
                    plan.DeliversHarnessPlugins = false;
                    AssertFalse(plan.Args("do the work").Contains("--plugin-dir"), "a remote-runner plan passes no plugin");
                    AssertFalse(plan.StartInfoWithEnvironment(null).Environment.ContainsKey(HarnessPlugins.ClaudeCodeFunctionHooksVariable),
                        "and no hooks switch the runner would refuse");

                    HarnessPlugins.Root = Path.Combine(root, "not-shipped");
                    AssertFalse(CreateRuntime().Args("do the work").Contains("--plugin-dir"), "no plugin, no flag");
                    AssertFalse(CreateRuntime().StartInfoWithEnvironment(null).Environment.ContainsKey(HarnessPlugins.ClaudeCodeFunctionHooksVariable),
                        "and no switch that would run some other plugin's hooks");
                }
                finally
                {
                    HarnessPlugins.Root = previous;
                    try { Directory.Delete(root, true); } catch (IOException) { }
                }
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

            await RunTest("ValidateReasoningEffort_High_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.ClaudeCode, "high");
                AssertNull(error, "high must be accepted for ClaudeCode");
            });

            await RunTest("BuildArguments_PromptContainsRolePreamble", () =>
            {
                string rolePreamble = "Role: You are an Armada worker agent.";
                string prompt = rolePreamble + " Mission: test objective. Branch: main.";
                InspectableClaudeCodeRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args(prompt);
                string lastArg = args[args.Count - 1];
                AssertTrue(lastArg.Contains(rolePreamble), "Claude Code prompt argument must contain the role preamble so the captain knows its role");
                AssertTrue(lastArg.Contains("Mission: test objective"), "Claude Code prompt argument must contain the mission instructions");
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

            await RunTest("IsRunningAsync Invalid ProcessId Returns False", async () =>
            {
                ClaudeCodeRuntime runtime = CreateRuntime();
                bool running = await runtime.IsRunningAsync(-1);
                AssertFalse(running);
            });
        }
    }
}
