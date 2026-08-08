namespace Armada.Test.Runtimes.Suites
{
    using System.Diagnostics;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    public class MuxRuntimeTests : TestSuite
    {
        public override string Name => "Mux Runtime Tests";

        private sealed class InspectableMuxRuntime : MuxRuntime
        {
            public InspectableMuxRuntime(LoggingModule logging) : base(logging)
            {
            }

            public List<string> Args(string workingDirectory, string prompt, string? model = null, string? finalMessageFilePath = null, Captain? captain = null) =>
                BuildArguments(workingDirectory, prompt, model, finalMessageFilePath, captain);

            public bool UsesPromptStdin() => UsePromptStdin;

            public void FeedUsage(int processId, string line) => HandleRawOutputLine(processId, line);

            public string? AppliedEnvironmentValue(Captain captain, string key)
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                ApplyEnvironment(startInfo, captain);

                if (startInfo.Environment.ContainsKey(key))
                {
                    return startInfo.Environment[key];
                }

                return null;
            }
        }

        private static InspectableMuxRuntime CreateRuntime()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InspectableMuxRuntime(logging);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Name Returns Mux", () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                AssertEqual("Mux", runtime.Name);
            });

            await RunTest("ExecutablePath Default Is Mux", () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                AssertEqual("mux", runtime.ExecutablePath);
            });

            await RunTest("BuildArguments Uses Current Mux Run Contract", () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                Captain captain = new Captain("mux-captain", AgentRuntimeEnum.Mux)
                {
                    RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new MuxCaptainOptions
                    {
                        ConfigDirectory = "C:/mux/config",
                        Endpoint = "captain-prod",
                        BaseUrl = "https://mux.example.com",
                        AdapterType = "openai",
                        Temperature = 0.2,
                        MaxTokens = 4096,
                        SystemPromptPath = "C:/mux/prompts/system.txt",
                        ApprovalPolicy = "deny"
                    })
                };

                List<string> args = runtime.Args("C:/worktree", "test prompt", "gpt-5.4-mini", "C:/logs/final.txt", captain);

                AssertEqual("print", args[0]);
                AssertTrue(args.Contains("--model"));
                AssertTrue(args.Contains("gpt-5.4-mini"));
                AssertTrue(args.Contains("-w"));
                AssertTrue(args.Contains("C:/worktree"));
                AssertTrue(args.Contains("--yolo"));
                AssertTrue(args.Contains("--config-dir"));
                AssertTrue(args.Contains("--output-format"));
                AssertTrue(args.Contains("jsonl"));
                AssertTrue(args.Contains("--output-last-message"));
                AssertTrue(args.Contains("--endpoint"));
                AssertFalse(args.Contains("--base-url"));
                AssertFalse(args.Contains("--adapter-type"));
                AssertFalse(args.Contains("--temperature"));
                AssertFalse(args.Contains("--max-tokens"));
                AssertFalse(args.Contains("--system-prompt"));
                AssertFalse(args.Contains("--approval-policy"));
                AssertTrue(args.Contains("test prompt"));
                AssertTrue(runtime.UsesPromptStdin());
            });

            await RunTest("BuildArguments Defaults To Exec Mode", () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                Captain captain = new Captain("mux-captain", AgentRuntimeEnum.Mux)
                {
                    RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new MuxCaptainOptions
                    {
                        Endpoint = "captain-prod"
                    })
                };

                List<string> args = runtime.Args("C:/worktree", "test prompt", captain: captain);

                AssertTrue(args.Contains("--yolo"));
                AssertFalse(args.Contains("--approval-policy"));
                AssertFalse(args.Contains("--mode"));
            });

            await RunTest("BuildArguments IgnoresLegacyPlanApproval", () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                Captain captain = new Captain("mux-captain", AgentRuntimeEnum.Mux)
                {
                    RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new MuxCaptainOptions
                    {
                        ApprovalPolicy = "plan"
                    })
                };

                List<string> args = runtime.Args("C:/worktree", "test prompt", captain: captain);

                AssertFalse(args.Contains("--mode"));
                AssertFalse(args.Contains("plan"));
            });

            await RunTest("ExactProviderUsageIsPublishedButEstimatesAreIgnored", () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                RuntimeTokenUsage? captured = null;
                runtime.OnTokenUsageReceived += (_, usage) => captured = usage;
                runtime.FeedUsage(21, "{\"eventType\":\"llm_response\",\"usage\":{\"inputTokens\":100,\"outputTokens\":20}}");
                AssertNotNull(captured);
                AssertEqual(100L, captured!.InputTokens);
                AssertEqual(20L, captured.OutputTokens);

                captured = null;
                runtime.FeedUsage(21, "{\"eventType\":\"run_completed\",\"finalEstimatedTokens\":999}");
                AssertNull(captured, "Mux estimates must never be recorded as authoritative usage");
            });

            await RunTest("ApplyEnvironment Maps Config And BaseUrl", () =>
            {
                InspectableMuxRuntime runtime = CreateRuntime();
                Captain captain = new Captain("mux-captain", AgentRuntimeEnum.Mux)
                {
                    RuntimeOptionsJson = CaptainRuntimeOptions.Serialize(new MuxCaptainOptions
                    {
                        ConfigDirectory = "C:/mux/config",
                        BaseUrl = "https://cun.ai/v1"
                    })
                };

                AssertEqual("C:/mux/config", runtime.AppliedEnvironmentValue(captain, "MUX_CONFIG_ROOT"));
                AssertEqual("https://cun.ai/v1", runtime.AppliedEnvironmentValue(captain, "OPENAI_BASE_URL"));
            });
        }
    }
}
