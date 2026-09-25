namespace Armada.Test.Runtimes.Suites
{
    using System.IO;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    public class CodexRuntimeTests : TestSuite
    {
        public override string Name => "Codex Runtime Tests";

        private sealed class InspectableCodexRuntime : CodexRuntime
        {
            public InspectableCodexRuntime(LoggingModule logging) : base(logging)
            {
            }

            public string Command() => GetCommand();

            public List<string> Args(string prompt, string? model = null, string? finalMessageFilePath = null, Captain? captain = null) =>
                BuildArguments(Path.GetTempPath(), prompt, model, finalMessageFilePath, captain);

            public bool StdinRedirected => RedirectStdin;

            public bool PromptViaStdin => UsePromptStdin;

            public bool StderrWrittenToLogFile => WriteStderrToLogFile;

            public void FeedUsage(int processId, string line) => HandleRawOutputLine(processId, line);

            public string TransformLine(string line) => TransformOutputLine(line);
        }

        private InspectableCodexRuntime CreateRuntime()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InspectableCodexRuntime(logging);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("A Prompt Longer Than The Windows Command Line Limit Is Delivered On Stdin", () =>
            {
                // Windows caps a whole command line at 32,767 characters and cmd.exe, which runs the
                // npm .cmd shim, at 8,191; a mission brief routinely exceeds both.
                string prompt = "Role: You are an Armada worker agent. Mission: " + new string('x', 40000);
                InspectableCodexRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args(prompt, "gpt-5.4", Path.Combine(Path.GetTempPath(), "final.txt"));
                AssertTrue(runtime.StdinRedirected && runtime.PromptViaStdin, "the prompt must reach Codex on stdin");
                AssertEqual("-", args[args.Count - 1], "Codex reads its instructions from stdin when the prompt argument is '-'");
                AssertFalse(args.Any(arg => arg.Contains("Mission: ")), "no argument may carry the prompt");
                AssertTrue(String.Join(" ", args).Length < 8191, "the command line must stay inside the cmd.exe limit");
            });

            await RunTest("BuildArguments Uses Json Flag WithReadableTransformation", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                AssertTrue(args.Contains("--json"), "Codex JSONL is required for exact usage telemetry");
                AssertEqual("[ARMADA:PROGRESS] 50", runtime.TransformLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"[ARMADA:PROGRESS] 50\"}}"));
            });

            await RunTest("ErrorEvent WithMessage PersistsProviderErrorText", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                string line = runtime.TransformLine("{\"type\":\"error\",\"message\":\"Request failed with status code 429: insufficient balance\"}");
                AssertContains("[ARMADA:ACTIVITY] codex error", line, "Error event must keep its activity marker");
                AssertContains("status code 429", line, "Error event must persist the provider error payload, not reduce it to the bare event type");
            });

            await RunTest("ErrorEvent WithNestedErrorPayload PersistsMessage", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                string line = runtime.TransformLine("{\"type\":\"error\",\"error\":{\"message\":\"model not found: gpt-5.6-sol\"}}");
                AssertContains("model not found", line, "Nested error payload must be persisted");
            });

            await RunTest("ErrorEvent WithoutMessage FallsBackToBareType", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                AssertEqual("[ARMADA:ACTIVITY] codex error", runtime.TransformLine("{\"type\":\"error\"}"));
            });

            await RunTest("TurnCompleted PublishesExactUsage", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                RuntimeTokenUsage? captured = null;
                runtime.OnTokenUsageReceived += (_, usage) => captured = usage;
                runtime.FeedUsage(42, "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":15567,\"cached_input_tokens\":13056,\"cache_write_input_tokens\":12,\"output_tokens\":5,\"reasoning_output_tokens\":2}}");
                AssertNotNull(captured);
                AssertEqual(TokenUsageRuleEnum.SeparateInputBuckets, captured!.UsageRule, "Codex usage is split into input buckets");
                AssertEqual(2499L, captured.UncachedInputTokens, "Codex uncached input bucket");
                AssertEqual(13056L, captured.CacheReadTokens, "Codex cache-read input bucket");
                AssertEqual(12L, captured.CacheWriteTokens, "Codex cache-write input bucket");
                AssertEqual(15567L, captured.InputTokens, "Codex input is the sum of the three buckets");
                AssertEqual(5L, captured.OutputTokens);
                AssertEqual(2L, captured.ReasoningTokens);
            });

            await RunTest("WriteStderrToLogFile Returns False", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                AssertFalse(runtime.StderrWrittenToLogFile, "Codex streams its full transcript on stderr; the log-file write must be suppressed to keep mission logs bounded");
            });

            await RunTest("BuildArguments Uses Exec With Platform Appropriate Auto Mode", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                AssertEqual("exec", args[0]);

                // Mirror the runtime's own condition. It bypasses on Linux as well as Windows,
                // because Codex's nested bubblewrap sandbox fails on user-namespace and loopback
                // setup inside a container. Testing only for Windows meant this assertion was
                // wrong on Linux -- the platform the admiral actually runs on -- so the suite
                // could never pass there.
                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                    AssertTrue(args.Contains("--dangerously-bypass-approvals-and-sandbox"));
                else
                    AssertTrue(args.Contains("--full-auto"));
            });

            await RunTest("BuildArguments Dangerous Uses Dangerous Flag", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                runtime.ApprovalMode = "dangerous";
                List<string> args = runtime.Args("test prompt");
                AssertEqual("exec", args[0]);
                AssertTrue(args.Contains("--dangerously-bypass-approvals-and-sandbox"));
            });

            await RunTest("ValidateReasoningEffort_High_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.Codex, "high");
                AssertNull(error, "high must be accepted for Codex");
            });

            await RunTest("ValidateReasoningEffort_Xhigh_ReturnsError", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.Codex, "xhigh");
                AssertNotNull(error, "xhigh must be rejected for Codex");
                AssertContains("Accepted values: low, medium, high.", error!, "Error should list the supported values");
            });

            await RunTest("ValidateReasoningEffort_Max_ReturnsError", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.Codex, "max");
                AssertNotNull(error, "max must be rejected for Codex");
                AssertContains("Accepted values: low, medium, high.", error!, "Error should list the supported values");
            });

            await RunTest("BuildArguments_HighReasoningEffort_UsesModelReasoningEffortConfig", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                Captain captain = new Captain("codex", AgentRuntimeEnum.Codex);
                captain.RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new CaptainOptions
                {
                    ReasoningEffort = "high"
                });

                List<string> args = runtime.Args("test prompt", captain: captain);

                AssertTrue(args.Contains("-c"), "Codex config flag should be present");
                AssertTrue(args.Contains("model_reasoning_effort=high"), "Codex should receive the effective reasoning config key");
                AssertFalse(args.Contains("reasoning_effort=high"), "Codex should not receive the old reasoning config key");
            });
        }
    }
}
