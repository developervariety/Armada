namespace Armada.Test.Runtimes.Suites
{
    using System.IO;
    using Armada.Core.Models;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    public class GeminiRuntimeTests : TestSuite
    {
        public override string Name => "Gemini Runtime Tests";

        private sealed class InspectableGeminiRuntime : GeminiRuntime
        {
            public InspectableGeminiRuntime(LoggingModule logging) : base(logging)
            {
            }

            public string Command() => GetCommand();

            public List<string> Args(string prompt, string? model = null, string? finalMessageFilePath = null) =>
                BuildArguments(Path.GetTempPath(), prompt, model, finalMessageFilePath, null);

            public void FeedUsage(int processId, string line) => HandleRawOutputLine(processId, line);
        }

        private InspectableGeminiRuntime CreateRuntime()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InspectableGeminiRuntime(logging);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("ApprovalMode Default Is Yolo", () =>
            {
                InspectableGeminiRuntime runtime = CreateRuntime();
                AssertEqual("yolo", runtime.ApprovalMode);
            });

            await RunTest("BuildArguments Uses Prompt And ApprovalMode", () =>
            {
                InspectableGeminiRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                AssertEqual("-p", args[0]);
                AssertEqual("test prompt", args[1]);
                AssertTrue(args.Contains("--approval-mode"));
                AssertTrue(args.Contains("yolo"));
                AssertTrue(args.Contains("stream-json"));
            });

            await RunTest("ResultPublishesExactPerModelUsage", () =>
            {
                InspectableGeminiRuntime runtime = CreateRuntime();
                RuntimeTokenUsage? captured = null;
                runtime.OnTokenUsageReceived += (_, usage) => captured = usage;
                runtime.FeedUsage(9, "{\"type\":\"result\",\"stats\":{\"models\":{\"gemini-2.5-pro\":{\"total_tokens\":40,\"input_tokens\":30,\"output_tokens\":10,\"cached\":5}}}}");
                AssertNotNull(captured);
                AssertEqual("gemini-2.5-pro", captured!.Model);
                AssertEqual(40L, captured.ProviderTotalTokens);
                AssertEqual(5L, captured.CacheReadTokens);
            });

            await RunTest("BuildArguments Includes Model When Supplied", () =>
            {
                InspectableGeminiRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "gemini-2.5-pro");
                int modelIndex = args.IndexOf("--model");
                AssertTrue(modelIndex >= 0);
                AssertEqual("gemini-2.5-pro", args[modelIndex + 1]);
            });

            await RunTest("Windows Command Resolves Cmd Wrapper", () =>
            {
                InspectableGeminiRuntime runtime = CreateRuntime();
                string command = runtime.Command();

                if (OperatingSystem.IsWindows())
                    AssertTrue(command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || command.Equals("gemini", StringComparison.OrdinalIgnoreCase), "Expected gemini command to resolve to .cmd or gemini");
                else
                    AssertEqual("gemini", command);
            });
        }
    }
}
