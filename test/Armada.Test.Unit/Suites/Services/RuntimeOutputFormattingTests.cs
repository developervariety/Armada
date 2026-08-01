namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Verifies that every runtime writes ONE activity shape into the mission log:
    /// <c>[ARMADA:ACTIVITY] tool &lt;name&gt; &lt;detail&gt; (&lt;status&gt;)</c>, with a canonical
    /// tool vocabulary, dock-relative paths, and the status words ok / error / error exit N.
    /// </summary>
    public class RuntimeOutputFormattingTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Runtime Output Formatting";

        private const string _Dock = "/work/example-dock";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("OpenCode_SuppressesStepsAndKeepsNamedTools", () =>
            {
                TestOpenCodeRuntime runtime = new TestOpenCodeRuntime();
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"step_start\"}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool read README.md (ok)",
                    runtime.Format("{\"type\":\"tool_use\",\"part\":{\"type\":\"tool\",\"tool\":\"read\",\"state\":{\"status\":\"completed\",\"input\":{\"filePath\":\"README.md\"}}}}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool bash git status (error)",
                    runtime.Format("{\"type\":\"tool_use\",\"part\":{\"type\":\"tool\",\"tool\":\"bash\",\"state\":{\"status\":\"failed\",\"input\":{\"command\":\"git status\"}}}}"));
                AssertEqual(
                    "Useful explanation",
                    runtime.Format("{\"type\":\"text\",\"part\":{\"type\":\"text\",\"text\":\"Useful explanation\"}}"));
                return Task.CompletedTask;
            });

            await RunTest("OpenCode_DropsReasoningWithoutLeakingJson", () =>
            {
                TestOpenCodeRuntime runtime = new TestOpenCodeRuntime();
                AssertEqual(
                    String.Empty,
                    runtime.Format("{\"type\":\"reasoning\",\"part\":{\"type\":\"reasoning\",\"text\":\"Private deliberation.\"}}"),
                    "Reasoning must be dropped, and must not fall through to the raw-line branch");
                return Task.CompletedTask;
            });

            // cursor-agent names a tool by the KEY of its payload object and exposes no "name"
            // property, so the generic finder matched nothing and every Cursor tool call was
            // silently dropped. These cases pin the real CLI shape.
            await RunTest("Cursor_RendersToolCallsFromKeyedPayload", () =>
            {
                TestCursorRuntime runtime = new TestCursorRuntime();
                runtime.SetDock(_Dock);

                AssertEqual(
                    "[ARMADA:ACTIVITY] tool read sample.txt (ok)",
                    runtime.Format("{\"type\":\"tool_call\",\"subtype\":\"completed\",\"call_id\":\"c1\",\"tool_call\":{\"readToolCall\":{\"args\":{\"path\":\"" + _Dock + "/sample.txt\"},\"result\":{\"success\":{\"content\":\"hello\"}}},\"toolCallId\":\"c1\",\"hookAdditionalContexts\":[],\"startedAtMs\":\"1785547476498\"}}"));

                AssertEqual(
                    "[ARMADA:ACTIVITY] tool bash ls -la (ok)",
                    runtime.Format("{\"type\":\"tool_call\",\"subtype\":\"completed\",\"tool_call\":{\"shellToolCall\":{\"args\":{\"command\":\"ls -la\",\"workingDirectory\":\"\"},\"result\":{\"success\":{}}}}}"));

                AssertEqual(
                    "[ARMADA:ACTIVITY] tool bash false (error)",
                    runtime.Format("{\"type\":\"tool_call\",\"subtype\":\"completed\",\"tool_call\":{\"shellToolCall\":{\"args\":{\"command\":\"false\"},\"result\":{\"error\":{\"message\":\"exit 1\"}}}}}"));

                AssertEqual(
                    String.Empty,
                    runtime.Format("{\"type\":\"tool_call\",\"subtype\":\"started\",\"tool_call\":{\"readToolCall\":{\"args\":{\"path\":\"sample.txt\"}}}}"),
                    "The started event duplicates the completed one and carries no outcome");

                AssertEqual(
                    String.Empty,
                    runtime.Format("{\"type\":\"thinking\",\"subtype\":\"delta\",\"text\":\"Private deliberation.\"}"));

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
                    "[ARMADA:ACTIVITY] tool bash git status",
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
                    "[ARMADA:ACTIVITY] tool bash git status",
                    runtime.Format("{\"eventType\":\"tool_call\",\"tool\":\"bash\",\"input\":{\"command\":\"git status\"}}"));
                AssertEqual("Mux summary", runtime.Format("{\"type\":\"assistant\",\"text\":\"Mux summary\"}"));
                return Task.CompletedTask;
            });

            // Claude Code reports a call and its outcome as two events. The call is held until
            // the result arrives so the rendered line carries a status, like every other runtime.
            await RunTest("Claude_CorrelatesToolCallWithItsResult", () =>
            {
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();

                AssertEqual(
                    String.Empty,
                    runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"tu_1\",\"name\":\"Read\",\"input\":{\"file_path\":\"src/Program.cs\"}}]}}"),
                    "A call with no outcome yet must not be rendered");

                AssertEqual(
                    "[ARMADA:ACTIVITY] tool read src/Program.cs (ok)",
                    runtime.Format("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu_1\",\"content\":\"file contents\"}]}}"));

                runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"tu_2\",\"name\":\"Bash\",\"input\":{\"command\":\"ls /nope\"}}]}}");
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool bash ls /nope (error)",
                    runtime.Format("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu_2\",\"is_error\":true,\"content\":\"No such file\"}]}}"),
                    "A failure must name the tool that failed");
                return Task.CompletedTask;
            });

            await RunTest("Claude_UnfinishedToolCallIsFlushedOnResult", () =>
            {
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();
                runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"tu_9\",\"name\":\"Grep\",\"input\":{\"pattern\":\"TODO\"}}]}}");

                string[] records = runtime.FormatRecords("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":3}");

                AssertEqual(2, records.Length);
                AssertEqual("[ARMADA:ACTIVITY] tool grep TODO (incomplete)", records[0]);
                AssertEqual("[ARMADA:ACTIVITY] claude result success (3 turns)", records[1]);
                return Task.CompletedTask;
            });

            await RunTest("Claude_UnfinishedToolCallIsFlushedOnProcessExit", () =>
            {
                // A killed captain never emits its result event, and the call in flight is
                // usually why it died. It must still reach the log.
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();
                runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"tu_hang\",\"name\":\"Bash\",\"input\":{\"command\":\"dotnet test\"}}]}}");

                string[] records = runtime.ExitRecords();

                AssertEqual(1, records.Length);
                AssertEqual("[ARMADA:ACTIVITY] tool bash dotnet test (incomplete)", records[0]);
                AssertEqual(0, runtime.ExitRecords().Length, "Flushed calls must not be written twice");
                return Task.CompletedTask;
            });

            await RunTest("Claude_ToolCallWithoutIdRendersImmediately", () =>
            {
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool mcp__armada__armada_status",
                    runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"mcp_tool_use\",\"name\":\"mcp__armada__armada_status\",\"input\":{}}]}}"),
                    "With nothing to correlate on, the call must still be logged");
                return Task.CompletedTask;
            });

            await RunTest("Claude_TextAndToolUse_BecomeSeparateRecords", () =>
            {
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();
                string[] records = runtime.FormatRecords("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"[ARMADA:PROGRESS] 50\"},{\"type\":\"tool_use\",\"id\":\"tu_3\",\"name\":\"Grep\",\"input\":{\"pattern\":\"ARMADA:ACTIVITY\"}}]}}");

                AssertEqual(1, records.Length, "The held tool call contributes no record yet");
                AssertEqual("[ARMADA:PROGRESS] 50", records[0], "A protocol marker must stay on its own record");

                AssertEqual(
                    "[ARMADA:ACTIVITY] tool grep ARMADA:ACTIVITY (ok)",
                    runtime.Format("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu_3\"}]}}"));
                return Task.CompletedTask;
            });

            await RunTest("Claude_SuppressesEnvelopeNoiseAndReasoning", () =>
            {
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-opus-5\"}"));
                AssertEqual(
                    String.Empty,
                    runtime.Format("{\"type\":\"system\",\"subtype\":\"thinking_tokens\",\"tokens\":128000}"),
                    "Any system subtype is session bookkeeping; this one appeared 20+ times in one mission");
                AssertEqual(
                    String.Empty,
                    runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"thinking\",\"thinking\":\"Weighing the two ports.\",\"signature\":\"sig\"}]}}"),
                    "Reasoning is private deliberation and is dropped by every runtime");
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"user\",\"message\":{\"content\":\"plain string content\"}}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool result (error)",
                    runtime.Format("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"unknown\",\"is_error\":true,\"content\":\"File does not exist\"}]}}"),
                    "A result with no matching call still has to surface the failure");
                AssertEqual(
                    "[ARMADA:ACTIVITY] claude rate limit event",
                    runtime.Format("{\"type\":\"rate_limit_event\"}"),
                    "Unrecognized event types must stay visible");
                return Task.CompletedTask;
            });

            await RunTest("Codex_ItemsRenderNamedActivity", () =>
            {
                TestCodexRuntime runtime = new TestCodexRuntime();
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool bash git status (ok)",
                    runtime.Format("{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"command\":\"git status\",\"exit_code\":0,\"aggregated_output\":\"on branch main\"}}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool bash false (error exit 1)",
                    runtime.Format("{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"command\":\"false\",\"exit_code\":1}}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool edit src/One.cs, src/Two.cs (ok)",
                    runtime.Format("{\"type\":\"item.completed\",\"item\":{\"type\":\"file_change\",\"status\":\"completed\",\"changes\":[{\"path\":\"src/One.cs\",\"kind\":\"update\"},{\"path\":\"src/Two.cs\",\"kind\":\"add\"}]}}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool armada.armada_status",
                    runtime.Format("{\"type\":\"item.completed\",\"item\":{\"type\":\"mcp_tool_call\",\"server\":\"armada\",\"tool\":\"armada_status\"}}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool websearch ISO15765 login frame",
                    runtime.Format("{\"type\":\"item.completed\",\"item\":{\"type\":\"web_search\",\"query\":\"ISO15765 login frame\"}}"));
                AssertEqual(
                    String.Empty,
                    runtime.Format("{\"type\":\"item.completed\",\"item\":{\"type\":\"reasoning\",\"text\":\"Private deliberation.\"}}"));
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\",\"command\":\"git status\"}}"));
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"turn.started\"}"));
                AssertEqual(
                    "Codex summary",
                    runtime.Format("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Codex summary\"}}"));
                return Task.CompletedTask;
            });

            // The same action must read identically whichever runtime performed it. Before this,
            // one grep produced four different lines across the four runtimes.
            await RunTest("AllRuntimes_RenderTheSameShellCallIdentically", () =>
            {
                string expected = "[ARMADA:ACTIVITY] tool bash git status (ok)";

                TestCodexRuntime codex = new TestCodexRuntime();
                TestOpenCodeRuntime openCode = new TestOpenCodeRuntime();
                TestCursorRuntime cursor = new TestCursorRuntime();
                TestClaudeCodeRuntime claude = new TestClaudeCodeRuntime();

                AssertEqual(
                    expected,
                    codex.Format("{\"type\":\"item.completed\",\"item\":{\"type\":\"command_execution\",\"command\":\"git status\",\"exit_code\":0}}"));
                AssertEqual(
                    expected,
                    openCode.Format("{\"type\":\"tool_use\",\"part\":{\"type\":\"tool\",\"tool\":\"bash\",\"state\":{\"status\":\"completed\",\"input\":{\"command\":\"git status\"}}}}"));
                AssertEqual(
                    expected,
                    cursor.Format("{\"type\":\"tool_call\",\"subtype\":\"completed\",\"tool_call\":{\"shellToolCall\":{\"args\":{\"command\":\"git status\"},\"result\":{\"success\":{}}}}}"));

                claude.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"tu_x\",\"name\":\"Bash\",\"input\":{\"command\":\"git status\"}}]}}");
                AssertEqual(
                    expected,
                    claude.Format("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu_x\"}]}}"));
                return Task.CompletedTask;
            });

            await RunTest("AllRuntimes_RenderPathsRelativeToTheDock", () =>
            {
                TestOpenCodeRuntime openCode = new TestOpenCodeRuntime();
                openCode.SetDock(_Dock);
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool read src/Example/ExampleClient.cs (ok)",
                    openCode.Format("{\"type\":\"tool_use\",\"part\":{\"type\":\"tool\",\"tool\":\"read\",\"state\":{\"status\":\"completed\",\"input\":{\"filePath\":\"" + _Dock + "/src/Example/ExampleClient.cs\"}}}}"),
                    "Repeating the dock root on every line buries the part a reader needs");

                TestClaudeCodeRuntime claude = new TestClaudeCodeRuntime();
                claude.SetDock(_Dock);
                claude.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"tu_p\",\"name\":\"Read\",\"input\":{\"file_path\":\"" + _Dock + "/CLAUDE.md\"}}]}}");
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool read CLAUDE.md (ok)",
                    claude.Format("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu_p\"}]}}"));
                return Task.CompletedTask;
            });

            await RunTest("Redaction_KeepsSecretsOutAndPathsIn", () =>
            {
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();

                string activity = runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"curl -H token=abcdefabcdefabcdefabcdefabcdefabcdef\"}}]}}");
                AssertContains("<redacted len=", activity, "Secret-shaped tool arguments must not reach durable telemetry");
                AssertFalse(activity.Contains("abcdefabcdefabcdefabcdefabcdefabcdef"), "Raw secret must not survive redaction");

                // The standalone high-entropy rule used to include '/' in its character class, so
                // one match spanned a whole filesystem path and the command was logged as
                // "<redacted len=81>" -- the reader could not tell what the captain searched.
                string search = runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"grep -rn pattern /opt/example/generated/sources/decoded\"}}]}}");
                AssertContains("/opt/example/generated/sources/decoded", search, "A long path is not a secret");
                AssertFalse(search.Contains("<redacted"), "A path must survive redaction intact");
                return Task.CompletedTask;
            });

            await RunTest("ExistingLogs_FilterEnvelopeOnlyActivityRecords", () =>
            {
                string[] filtered = RuntimeLogNoiseFilter.Filter(new[]
                {
                    "[ARMADA:ACTIVITY] claude assistant",
                    "[ARMADA:ACTIVITY] claude user",
                    "[ARMADA:ACTIVITY] claude system",
                    "[ARMADA:ACTIVITY] claude system thinking tokens",
                    "[ARMADA:ACTIVITY] codex item completed",
                    "[ARMADA:ACTIVITY] claude rate limit event",
                    "[ARMADA:ACTIVITY] tool read src/Program.cs (ok)",
                    "Useful captain explanation"
                });

                AssertEqual(3, filtered.Length);
                AssertEqual("[ARMADA:ACTIVITY] claude rate limit event", filtered[0]);
                AssertEqual("[ARMADA:ACTIVITY] tool read src/Program.cs (ok)", filtered[1]);
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
                    "[ARMADA:ACTIVITY] tool read README.md (ok)",
                    "Useful captain explanation",
                    "[ARMADA:RESULT] COMPLETE",
                    "[2026-07-29 05:45:37] Agent exited with code 0"
                });

                AssertEqual(4, filtered.Length);
                AssertEqual("[ARMADA:ACTIVITY] tool read README.md (ok)", filtered[0]);
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

            public void SetDock(string directory)
            {
                WorkingDirectory = directory;
            }

            public string Format(string line)
            {
                return TransformOutputLine(line);
            }

            public string[] FormatRecords(string line)
            {
                return TransformOutputRecords(line).ToArray();
            }

            public string[] ExitRecords()
            {
                return BuildProcessExitRecords().ToArray();
            }
        }

        private sealed class TestCodexRuntime : CodexRuntime
        {
            public TestCodexRuntime() : base(new LoggingModule())
            {
            }

            public void SetDock(string directory)
            {
                WorkingDirectory = directory;
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

            public void SetDock(string directory)
            {
                WorkingDirectory = directory;
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

            public void SetDock(string directory)
            {
                WorkingDirectory = directory;
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
