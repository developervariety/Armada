namespace Armada.Test.Runtimes.Suites
{
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

            await RunTest("BuildArguments Includes Endpoint Config And Final Message Artifact", () =>
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
                AssertTrue(args.Contains("--config-dir"));
                AssertTrue(args.Contains("C:/mux/config"));
                AssertTrue(args.Contains("--output-format"));
                AssertTrue(args.Contains("jsonl"));
                AssertTrue(args.Contains("--output-last-message"));
                AssertTrue(args.Contains("C:/logs/final.txt"));
                AssertTrue(args.Contains("--endpoint"));
                AssertTrue(args.Contains("captain-prod"));
                AssertTrue(args.Contains("--model"));
                AssertTrue(args.Contains("gpt-5.4-mini"));
                AssertTrue(args.Contains("--base-url"));
                AssertTrue(args.Contains("https://mux.example.com"));
                AssertTrue(args.Contains("--adapter-type"));
                AssertTrue(args.Contains("openai"));
                AssertTrue(args.Contains("--temperature"));
                AssertTrue(args.Contains("0.2"));
                AssertTrue(args.Contains("--max-tokens"));
                AssertTrue(args.Contains("4096"));
                AssertTrue(args.Contains("--system-prompt"));
                AssertTrue(args.Contains("C:/mux/prompts/system.txt"));
                AssertTrue(args.Contains("--approval-policy"));
                AssertTrue(args.Contains("deny"));
                AssertEqual("test prompt", args[args.Count - 1]);
            });

            await RunTest("BuildArguments Defaults To Yolo Approval", () =>
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
            });
        }
    }
}
