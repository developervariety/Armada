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

            if (OperatingSystem.IsWindows())
            {
                SkipTest("ProcessProviderError_UsesExplicitGoogleRpcStderr", "the provider stub is a POSIX shell script");
            }
            else await RunTest("ProcessProviderError_UsesExplicitGoogleRpcStderr", async () =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "armada-gemini-provider-error-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    string executable = FinalReplyProcessTestHelper.CreateExecutable(directory, new[]
                    {
                        "{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"HTTP 429 is narration only\"}",
                        "{\"type\":\"result\",\"status\":\"error\",\"error\":\"quota exceeded\"}"
                    }, 1, new[] { "RESOURCE_EXHAUSTED: provider refused the request", "{\"error\":{\"status\":\"UNAUTHENTICATED\"}}" });
                    InspectableGeminiRuntime runtime = CreateRuntime();
                    runtime.ExecutablePath = executable;
                    List<RuntimeProviderError> captured = new List<RuntimeProviderError>();
                    runtime.OnProviderErrorReceived += (_, error) => captured.Add(error);

                    await FinalReplyProcessTestHelper.StartAndWaitAsync(runtime, directory, Path.Combine(directory, "final.txt")).ConfigureAwait(false);

                    AssertEqual(2, captured.Count, "Only explicit RPC error codes on stderr provide evidence.");
                    AssertEqual("rate_limit_exceeded", captured[0].ErrorType);
                    AssertEqual("authentication_error", captured[1].ErrorType);
                    AssertNull(captured[0].HttpStatusCode);
                }
                finally
                {
                    Directory.Delete(directory, true);
                }
            });

            if (OperatingSystem.IsWindows())
            {
                SkipTest("ProcessFinalReply_CommitsOnlyTheSuccessfulAssistantTurn", "the provider stub is a POSIX shell script");
            }
            else await RunTest("ProcessFinalReply_CommitsOnlyTheSuccessfulAssistantTurn", async () =>
            {
                string directory = Path.Combine(Path.GetTempPath(), "armada-gemini-final-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                string finalPath = Path.Combine(directory, "final.txt");
                try
                {
                    string executable = FinalReplyProcessTestHelper.CreateExecutable(directory, new[]
                    {
                        "{\"type\":\"message\",\"role\":\"assistant\",\"delta\":true,\"content\":\"draft before tool\"}",
                        "{\"type\":\"tool_use\",\"tool_name\":\"read_file\",\"tool_id\":\"tool-1\",\"parameters\":{\"file_path\":\"secret.txt\"}}",
                        "{\"type\":\"message\",\"role\":\"assistant\",\"delta\":true,\"content\":\"discarded after first tool\"}",
                        "{\"type\":\"tool_use\",\"tool_name\":\"search\",\"tool_id\":\"tool-2\",\"parameters\":{\"query\":\"private\"}}",
                        "{\"type\":\"message\",\"role\":\"assistant\",\"delta\":true,\"content\":\"partial answer\"}",
                        "{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"Gemini final answer\"}",
                        "{\"type\":\"result\",\"status\":\"success\",\"stats\":{\"models\":{\"gemini-2.5-pro\":{\"total_tokens\":40,\"input_tokens\":30,\"output_tokens\":10,\"cached\":5}}}}"
                    }, 0);
                    InspectableGeminiRuntime runtime = CreateRuntime();
                    runtime.ExecutablePath = executable;
                    RuntimeTokenUsage? processUsage = null;
                    runtime.OnTokenUsageReceived += (_, usage) => processUsage = usage;

                    int? exitCode = await FinalReplyProcessTestHelper.StartAndWaitAsync(runtime, directory, finalPath).ConfigureAwait(false);
                    AssertEqual((int?)0, exitCode, "A successful Gemini result exits successfully.");
                    AssertEqual("Gemini final answer", File.ReadAllText(finalPath), "The final snapshot replaces prior turn text and excludes tool and draft text.");
                    AssertNotNull(processUsage, "The terminal result still publishes provider usage.");
                    AssertEqual("gemini-2.5-pro", processUsage!.Model, "Final-reply capture keeps the terminal usage model.");

                    File.Delete(finalPath);
                    string repeatedExecutable = FinalReplyProcessTestHelper.CreateExecutable(directory, new[]
                    {
                        "{\"type\":\"message\",\"role\":\"assistant\",\"delta\":true,\"content\":\"same\"}",
                        "{\"type\":\"message\",\"role\":\"assistant\",\"delta\":true,\"content\":\"same\"}",
                        "{\"type\":\"result\",\"status\":\"success\"}"
                    }, 0);
                    runtime.ExecutablePath = repeatedExecutable;
                    exitCode = await FinalReplyProcessTestHelper.StartAndWaitAsync(runtime, directory, finalPath).ConfigureAwait(false);
                    AssertEqual((int?)0, exitCode, "A second successful Gemini result exits successfully.");
                    AssertEqual("samesame", File.ReadAllText(finalPath), "Repeated assistant text is preserved as two legitimate deltas.");

                    File.WriteAllText(finalPath, "keep existing artifact");
                    string failedExecutable = FinalReplyProcessTestHelper.CreateExecutable(directory, new[]
                    {
                        "{\"type\":\"message\",\"role\":\"assistant\",\"delta\":true,\"content\":\"must not be saved\"}",
                        "{\"type\":\"result\",\"status\":\"error\"}"
                    }, 0);
                    runtime.ExecutablePath = failedExecutable;
                    exitCode = await FinalReplyProcessTestHelper.StartAndWaitAsync(runtime, directory, finalPath).ConfigureAwait(false);
                    AssertEqual((int?)0, exitCode, "The stub can exit normally after a provider error result.");
                    AssertEqual("keep existing artifact", File.ReadAllText(finalPath), "A failed Gemini result does not replace a pre-existing artifact.");
                }
                finally
                {
                    Directory.Delete(directory, true);
                }
            });
        }
    }
}
