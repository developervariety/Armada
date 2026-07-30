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

            await RunTest("Claude_AssistantToolUse_RendersNamedToolActivity", () =>
            {
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool Read src/Armada.Runtimes/ClaudeCodeRuntime.cs",
                    runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{\"file_path\":\"src/Armada.Runtimes/ClaudeCodeRuntime.cs\"}}]}}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool Bash dotnet build src/Armada.sln",
                    runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"dotnet build src/Armada.sln\",\"description\":\"Build\"}}]}}"));
                AssertEqual(
                    "Reading the mission brief.",
                    runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Reading the mission brief.\"}]}}"));
                AssertEqual(
                    "Weighing the two ports.",
                    runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"thinking\",\"thinking\":\"Weighing the two ports.\",\"signature\":\"sig\"}]}}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool mcp__armada__armada_status",
                    runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"mcp_tool_use\",\"name\":\"mcp__armada__armada_status\",\"input\":{}}]}}"));
                AssertEqual(
                    String.Empty,
                    runtime.Format("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"file\"}}}"),
                    "Partial-message deltas duplicate the completed assistant event");
                return Task.CompletedTask;
            });

            await RunTest("Claude_TextAndToolUse_BecomeSeparateRecords", () =>
            {
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();
                string[] records = runtime.FormatRecords("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"[ARMADA:PROGRESS] 50\"},{\"type\":\"tool_use\",\"name\":\"Grep\",\"input\":{\"pattern\":\"ARMADA:ACTIVITY\"}}]}}");

                AssertEqual(2, records.Length, "Text and tool activity must not share one record, or the protocol marker stops parsing");
                AssertEqual("[ARMADA:PROGRESS] 50", records[0]);
                AssertEqual("[ARMADA:ACTIVITY] tool Grep ARMADA:ACTIVITY", records[1]);
                return Task.CompletedTask;
            });

            await RunTest("Claude_SuppressesEnvelopeNoiseAndKeepsFailures", () =>
            {
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-opus-5\"}"));
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu_1\",\"content\":\"file contents\"}]}}"));
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"user\",\"message\":{\"content\":\"plain string content\"}}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool result (error)",
                    runtime.Format("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu_1\",\"is_error\":true,\"content\":\"File does not exist\"}]}}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] claude result success (17 turns)",
                    runtime.Format("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":17,\"result\":\"done\"}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] claude rate limit event",
                    runtime.Format("{\"type\":\"rate_limit_event\"}"),
                    "Unrecognized event types must stay visible");
                return Task.CompletedTask;
            });

            await RunTest("Claude_ToolActivity_RedactsSecretShapedArguments", () =>
            {
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();
                string activity = runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"curl -H token=abcdefabcdefabcdefabcdefabcdefabcdef\"}}]}}");

                AssertContains("<redacted len=", activity, "Secret-shaped tool arguments must not reach durable telemetry");
                AssertFalse(activity.Contains("abcdefabcdefabcdefabcdefabcdefabcdef"), "Raw secret must not survive redaction");
                return Task.CompletedTask;
            });

            await RunTest("Codex_ItemsRenderNamedActivity", () =>
            {
                TestCodexRuntime runtime = new TestCodexRuntime();
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool bash git status (exit 0)",
                    runtime.Format("{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"command\":\"git status\",\"exit_code\":0,\"aggregated_output\":\"on branch main\"}}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool edit src/One.cs, src/Two.cs (completed)",
                    runtime.Format("{\"type\":\"item.completed\",\"item\":{\"type\":\"file_change\",\"status\":\"completed\",\"changes\":[{\"path\":\"src/One.cs\",\"kind\":\"update\"},{\"path\":\"src/Two.cs\",\"kind\":\"add\"}]}}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool armada.armada_status",
                    runtime.Format("{\"type\":\"item.completed\",\"item\":{\"type\":\"mcp_tool_call\",\"server\":\"armada\",\"tool\":\"armada_status\"}}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool web_search ISO15765 login frame",
                    runtime.Format("{\"type\":\"item.completed\",\"item\":{\"type\":\"web_search\",\"query\":\"ISO15765 login frame\"}}"));
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\",\"command\":\"git status\"}}"));
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"turn.started\"}"));
                AssertEqual(
                    "Codex summary",
                    runtime.Format("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Codex summary\"}}"));
                return Task.CompletedTask;
            });

            await RunTest("ExistingLogs_FilterEnvelopeOnlyActivityRecords", () =>
            {
                string[] filtered = RuntimeLogNoiseFilter.Filter(new[]
                {
                    "[ARMADA:ACTIVITY] claude assistant",
                    "[ARMADA:ACTIVITY] claude user",
                    "[ARMADA:ACTIVITY] claude system",
                    "[ARMADA:ACTIVITY] codex item completed",
                    "[ARMADA:ACTIVITY] claude rate limit event",
                    "[ARMADA:ACTIVITY] tool Read src/Program.cs",
                    "Useful captain explanation"
                });

                AssertEqual(3, filtered.Length);
                AssertEqual("[ARMADA:ACTIVITY] claude rate limit event", filtered[0]);
                AssertEqual("[ARMADA:ACTIVITY] tool Read src/Program.cs", filtered[1]);
                AssertEqual("Useful captain explanation", filtered[2]);
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

        private sealed class TestClaudeCodeRuntime : ClaudeCodeRuntime
        {
            public TestClaudeCodeRuntime() : base(new LoggingModule())
            {
            }

            public string Format(string line)
            {
                return TransformOutputLine(line);
            }

            public string[] FormatRecords(string line)
            {
                return TransformOutputRecords(line).ToArray();
            }
        }

        private sealed class TestCodexRuntime : CodexRuntime
        {
            public TestCodexRuntime() : base(new LoggingModule())
            {
            }

            public string Format(string line)
            {
                return TransformOutputLine(line);
            }
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
