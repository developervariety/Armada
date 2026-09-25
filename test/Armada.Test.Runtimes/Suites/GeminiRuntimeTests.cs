namespace Armada.Test.Runtimes.Suites
{
    using System.IO;
    using Armada.Core.Enums;
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

            public bool PromptViaStdin => UsePromptStdin;

            public List<string> Args(string prompt, string? model = null, string? finalMessageFilePath = null) =>
                BuildArguments(Path.GetTempPath(), prompt, model, finalMessageFilePath, null);

            public void FeedUsage(int processId, string line) => HandleRawOutputLine(processId, line);

            public string TransformLine(string line) => TransformOutputLine(line);
        }

        private InspectableGeminiRuntime CreateRuntime()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InspectableGeminiRuntime(logging);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("A Prompt Longer Than The Windows Command Line Limit Is Delivered On Stdin", () =>
            {
                // Windows caps a whole command line at 32,767 characters and cmd.exe, which runs the
                // npm .cmd shim, at 8,191; a mission brief routinely exceeds both.
                string prompt = "Role: You are an Armada worker agent. Mission: " + new string('x', 40000);
                InspectableGeminiRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args(prompt, "gemini-2.5-pro");
                AssertTrue(runtime.PromptViaStdin, "the prompt must reach Gemini on stdin");
                AssertFalse(args.Any(arg => arg.Contains("Mission: ")), "no argument may carry the prompt");
                AssertTrue(String.Join(" ", args).Length < 8191, "the command line must stay inside the cmd.exe limit");
            });

            await RunTest("BuildArguments Uses ApprovalMode And Stream Json", () =>
            {
                InspectableGeminiRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
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
                AssertEqual(TokenUsageRuleEnum.SeparateInputBuckets, captured.UsageRule, "Gemini usage is split into input buckets");
                AssertEqual(25L, captured.UncachedInputTokens, "Gemini uncached input bucket");
                AssertEqual(5L, captured.CacheReadTokens, "Gemini cache-read input bucket");
                AssertEqual(0L, captured.CacheWriteTokens, "Gemini cache-write input bucket");
                AssertEqual(30L, captured.InputTokens, "Gemini input is the sum of the three buckets");
            });

            await RunTest("A JSON Error Event Reaches The Mission Log", () =>
            {
                InspectableGeminiRuntime runtime = CreateRuntime();
                string rendered = runtime.TransformLine("{\"type\":\"error\",\"message\":\"quota exceeded\"}");
                AssertContains("quota exceeded", rendered, "the provider's error text is kept");
                AssertContains("error", rendered, "the record reads as an error");
            });
        }
    }
}
