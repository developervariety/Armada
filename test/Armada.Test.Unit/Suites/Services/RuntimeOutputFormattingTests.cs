namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Verifies that structured runtimes retain useful content without lifecycle noise.
    /// </summary>
    public class RuntimeOutputFormattingTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Runtime Output Formatting";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("OpenCode_SuppressesStepsAndKeepsNamedTools", () =>
            {
                TestOpenCodeRuntime runtime = new TestOpenCodeRuntime();
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"step_start\"}"));
                AssertContains(
                    "tool read",
                    runtime.Format("{\"type\":\"tool_use\",\"part\":{\"type\":\"tool\",\"tool\":\"read\",\"state\":{\"status\":\"completed\",\"input\":{\"filePath\":\"README.md\"}}}}"));
                AssertEqual(
                    "Useful explanation",
                    runtime.Format("{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"Useful explanation\"}}"));
                return Task.CompletedTask;
            });

            await RunTest("Cursor_SuppressesThinkingAndKeepsNamedTools", () =>
            {
                TestCursorRuntime runtime = new TestCursorRuntime();
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"thinking\"}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool read_file",
                    runtime.Format("{\"type\":\"tool_call\",\"name\":\"read_file\",\"arguments\":{\"path\":\"README.md\"}}"));
                AssertEqual(
                    "Cursor summary",
                    runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Cursor summary\"}]}}"));
                return Task.CompletedTask;
            });

            await RunTest("Gemini_SuppressesLifecycleAndKeepsNamedTools", () =>
            {
                TestGeminiRuntime runtime = new TestGeminiRuntime();
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"result\",\"stats\":{}}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool run_shell_command",
                    runtime.Format("{\"type\":\"tool_call\",\"tool_name\":\"run_shell_command\",\"parameters\":{\"command\":\"git status\"}}"));
                AssertEqual(
                    "Gemini summary",
                    runtime.Format("{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"Gemini summary\"}"));
                return Task.CompletedTask;
            });

            await RunTest("Mux_SuppressesLifecycleAndKeepsNamedTools", () =>
            {
                TestMuxRuntime runtime = new TestMuxRuntime();
                AssertEqual(String.Empty, runtime.Format("{\"eventType\":\"step_start\"}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool bash",
                    runtime.Format("{\"eventType\":\"tool_call\",\"tool\":\"bash\",\"input\":{\"command\":\"git status\"}}"));
                AssertEqual("Mux summary", runtime.Format("{\"type\":\"assistant\",\"text\":\"Mux summary\"}"));
                return Task.CompletedTask;
            });

            await RunTest("ExistingLogs_FilterSyntheticNoiseOnly", () =>
            {
                string[] filtered = RuntimeLogNoiseFilter.Filter(new[]
                {
                    "[ARMADA:ACTIVITY] step started",
                    "[ARMADA:ACTIVITY] cursor thinking",
                    "[ARMADA:ACTIVITY] cursor tool call",
                    "[ARMADA:ACTIVITY] tool read README.md (completed)",
                    "Useful captain explanation",
                    "[ARMADA:RESULT] COMPLETE",
                    "[2026-07-29 05:45:37] Agent exited with code 0"
                });

                AssertEqual(4, filtered.Length);
                AssertEqual("[ARMADA:ACTIVITY] tool read README.md (completed)", filtered[0]);
                AssertEqual("Useful captain explanation", filtered[1]);
                AssertEqual("[ARMADA:RESULT] COMPLETE", filtered[2]);
                AssertContains("Agent exited with code 0", filtered[3]);
                return Task.CompletedTask;
            });
        }

        private sealed class TestOpenCodeRuntime : OpenCodeRuntime
        {
            public TestOpenCodeRuntime() : base(new LoggingModule())
            {
            }

            public string Format(string line)
            {
                return TransformOutputLine(line);
            }
        }

        private sealed class TestCursorRuntime : CursorRuntime
        {
            public TestCursorRuntime() : base(new LoggingModule())
            {
            }

            public string Format(string line)
            {
                return TransformOutputLine(line);
            }
        }

        private sealed class TestGeminiRuntime : GeminiRuntime
        {
            public TestGeminiRuntime() : base(new LoggingModule())
            {
            }

            public string Format(string line)
            {
                return TransformOutputLine(line);
            }
        }

        private sealed class TestMuxRuntime : MuxRuntime
        {
            public TestMuxRuntime() : base(new LoggingModule())
            {
            }

            public string Format(string line)
            {
                return TransformOutputLine(line);
            }
        }
    }
}
