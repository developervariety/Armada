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

            public bool PromptViaStdin => UsePromptStdin;

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
        }
    }
}
