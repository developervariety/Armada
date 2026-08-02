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
            await RunTest("Name Returns Codex", () =>
            {
                CodexRuntime runtime = CreateRuntime();
                AssertEqual("Codex", runtime.Name);
            });

            await RunTest("SupportsResume Returns False", () =>
            {
                CodexRuntime runtime = CreateRuntime();
                AssertFalse(runtime.SupportsResume);
            });

            await RunTest("RedirectStdin Returns False", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                AssertFalse(runtime.StdinRedirected, "Codex prompt is a CLI arg; stdin pipe must not be opened");
            });

            await RunTest("BuildArguments Uses Json Flag WithReadableTransformation", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                AssertTrue(args.Contains("--json"), "Codex JSONL is required for exact usage telemetry");
                AssertEqual("[ARMADA:PROGRESS] 50", runtime.TransformLine("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"[ARMADA:PROGRESS] 50\"}}"));
            });

            await RunTest("TurnCompleted PublishesExactUsage", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                RuntimeTokenUsage? captured = null;
                runtime.OnTokenUsageReceived += (_, usage) => captured = usage;
                runtime.FeedUsage(42, "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":15567,\"cached_input_tokens\":13056,\"cache_write_input_tokens\":12,\"output_tokens\":5,\"reasoning_output_tokens\":2}}");
                AssertNotNull(captured);
                AssertEqual(15567L, captured!.InputTokens);
                AssertEqual(5L, captured.OutputTokens);
                AssertEqual(2L, captured.ReasoningTokens);
                AssertEqual(13056L, captured.CacheReadTokens);
                AssertEqual(12L, captured.CacheWriteTokens);
            });

            await RunTest("WriteStderrToLogFile Returns False", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                AssertFalse(runtime.StderrWrittenToLogFile, "Codex streams its full transcript on stderr; the log-file write must be suppressed to keep mission logs bounded");
            });

            await RunTest("ExecutablePath Default Is Codex", () =>
            {
                CodexRuntime runtime = CreateRuntime();
                AssertEqual("codex", runtime.ExecutablePath);
            });

            await RunTest("ApprovalMode Default Is FullAuto", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                AssertEqual("full-auto", runtime.ApprovalMode);
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
                AssertEqual("test prompt", args[args.Count - 1]);
            });

            await RunTest("BuildArguments Dangerous Uses Dangerous Flag", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                runtime.ApprovalMode = "dangerous";
                List<string> args = runtime.Args("test prompt");
                AssertEqual("exec", args[0]);
                AssertTrue(args.Contains("--dangerously-bypass-approvals-and-sandbox"));
                AssertEqual("test prompt", args[args.Count - 1]);
            });

            await RunTest("BuildArguments Includes Model When Supplied", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "gpt-5.4");
                int modelIndex = args.IndexOf("--model");
                AssertTrue(modelIndex >= 0);
                AssertEqual("gpt-5.4", args[modelIndex + 1]);
            });

            await RunTest("BuildArguments Includes Final Message Path When Supplied", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "gpt-5.4", "C:/temp/final-message.txt");
                int outputIndex = args.IndexOf("--output-last-message");
                AssertTrue(outputIndex >= 0);
                AssertEqual("C:/temp/final-message.txt", args[outputIndex + 1]);
            });

            await RunTest("ValidateReasoningEffort_High_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.Codex, "high");
                AssertNull(error, "high must be accepted for Codex");
            });

            await RunTest("BuildArguments_PromptContainsRolePreamble", () =>
            {
                string rolePreamble = "Role: You are an Armada worker agent.";
                string prompt = rolePreamble + " Mission: test objective. Branch: main.";
                InspectableCodexRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args(prompt);
                string lastArg = args[args.Count - 1];
                AssertTrue(lastArg.Contains(rolePreamble), "Codex prompt argument must contain the role preamble so the captain knows its role");
                AssertTrue(lastArg.Contains("Mission: test objective"), "Codex prompt argument must contain the mission instructions");
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

            await RunTest("Windows Command Resolves Cmd Wrapper", () =>
            {
                InspectableCodexRuntime runtime = CreateRuntime();
                string command = runtime.Command();

                if (OperatingSystem.IsWindows())
                    AssertTrue(command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || command.Equals("codex", StringComparison.OrdinalIgnoreCase), "Expected codex command to resolve to .cmd or codex");
                else
                    AssertEqual("codex", command);
            });

            await RunTest("ExecutablePath Set Null Throws", () =>
            {
                CodexRuntime runtime = CreateRuntime();
                AssertThrows<ArgumentNullException>(() => runtime.ExecutablePath = null!);
            });

            await RunTest("IsRunningAsync Invalid ProcessId Returns False", async () =>
            {
                CodexRuntime runtime = CreateRuntime();
                bool running = await runtime.IsRunningAsync(-1);
                AssertFalse(running);
            });
        }
    }
}
