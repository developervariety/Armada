namespace Armada.Test.Unit.Suites.Services
{
    using Armada.Core.Services;
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

        private const string _Dock = "/work/fleet/example-dock";

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

            await RunTest("Gemini_ErroredResultReachesTheLog", () =>
            {
                TestGeminiRuntime runtime = new TestGeminiRuntime();
                string rendered = runtime.Format("{\"type\":\"result\",\"status\":\"error\",\"error\":{\"type\":\"FatalError\",\"message\":\"Quota exceeded for quota metric 'Generate Content API requests per minute'\"},\"stats\":{}}");
                AssertTrue(rendered.Contains("Quota exceeded"), "The fatal error text reaches the log");
                AssertTrue(ActivityRecords.IsProviderFailure(rendered), "The fatal error is the shared provider failure record");
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"result\",\"stats\":{}}"), "A successful result stays suppressed");
                return Task.CompletedTask;
            });

            await RunTest("Gemini_StreamedDeltasKeepMarkersOnTheirOwnLine", () =>
            {
                TestGeminiRuntime runtime = new TestGeminiRuntime();
                List<string> records = new List<string>();
                records.AddRange(runtime.FormatRecords("{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"All checks pass.\\n[ARMADA:RES\",\"delta\":true}"));
                records.AddRange(runtime.FormatRecords("{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"ULT] COMPLETE\",\"delta\":true}"));
                records.AddRange(runtime.FormatRecords("{\"type\":\"result\",\"stats\":{}}"));

                List<ProgressParser.ProgressSignal> signals = ProgressParser.ParseAll(String.Join("\n", records));
                AssertEqual(1, signals.Count, "The split marker is detected once");
                AssertEqual("result", signals[0].Type);
                AssertEqual("COMPLETE", signals[0].Value);
                AssertEqual(2, records.Count, "Each whole line is one record");
                AssertEqual("All checks pass.", records[0]);
                AssertEqual("[ARMADA:RESULT] COMPLETE", records[1]);
                return Task.CompletedTask;
            });

            await RunTest("Gemini_UnfinishedStreamedLineIsWrittenAtExit", () =>
            {
                TestGeminiRuntime runtime = new TestGeminiRuntime();
                AssertEqual(0, runtime.FormatRecords("{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"[ARMADA:RESULT] COMP\",\"delta\":true}").Length, "An unfinished line is held");
                AssertEqual(0, runtime.FormatRecords("{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"LETE\",\"delta\":true}").Length, "Still no line break");
                string[] exit = runtime.ExitRecords();
                AssertEqual(1, exit.Length, "The held line is written when the process exits");
                AssertEqual("[ARMADA:RESULT] COMPLETE", exit[0]);
                AssertEqual(0, runtime.ExitRecords().Length, "A flushed line is not written twice");
                return Task.CompletedTask;
            });

            await RunTest("Cursor_ErroredResultReachesTheLog", () =>
            {
                TestCursorRuntime runtime = new TestCursorRuntime();
                string rendered = runtime.Format("{\"type\":\"result\",\"subtype\":\"error\",\"is_error\":true,\"result\":\"Rate limit exceeded. Please try again later.\"}");
                AssertFalse(String.IsNullOrEmpty(rendered), "The errored result is not suppressed");
                AssertTrue(rendered.Contains("Rate limit"), "The provider's text is kept");
                AssertTrue(ActivityRecords.IsProviderFailure(rendered), "The errored result is the shared provider failure record");
                return Task.CompletedTask;
            });

            await RunTest("Codex_FailedTurnKeepsItsMessage", () =>
            {
                TestCodexRuntime runtime = new TestCodexRuntime();
                string rendered = runtime.Format("{\"type\":\"turn.failed\",\"error\":{\"message\":\"stream disconnected before completion: 429 Too Many Requests\"}}");
                AssertTrue(rendered.Contains("429"), "The failed turn's message is kept");
                AssertTrue(ActivityRecords.IsProviderFailure(rendered), "A failed turn is the shared provider failure record");
                return Task.CompletedTask;
            });

            await RunTest("Claude_NonTextToolArgumentDoesNotHideTheMarker", () =>
            {
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();
                string[] records = runtime.FormatRecords("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"[ARMADA:RESULT] COMPLETE\"},{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"mcp__docs__lookup\",\"input\":{\"query\":{\"text\":\"a\"}}}]}}");
                AssertTrue(records.Contains("[ARMADA:RESULT] COMPLETE"), "The marker is its own record");
                AssertFalse(records.Any(record => record.StartsWith("{", StringComparison.Ordinal)), "No record is the raw JSON line");
                return Task.CompletedTask;
            });

            await RunTest("OpenCode_NonTextToolArgumentKeepsToolOutputOutOfTheLog", () =>
            {
                TestOpenCodeRuntime runtime = new TestOpenCodeRuntime();
                string rendered = runtime.Format("{\"type\":\"tool_use\",\"part\":{\"type\":\"tool\",\"tool\":\"docs_lookup\",\"state\":{\"status\":\"completed\",\"input\":{\"key\":7},\"output\":\"<tool output>\"}}}");
                AssertTrue(rendered.StartsWith("[ARMADA:ACTIVITY] tool", StringComparison.Ordinal), "The event is a tool record: " + rendered);
                AssertFalse(rendered.Contains("<tool output>"), "Tool output never reaches the log");
                return Task.CompletedTask;
            });

            await RunTest("Cursor_NonTextToolArgumentKeepsToolOutputOutOfTheLog", () =>
            {
                TestCursorRuntime runtime = new TestCursorRuntime();
                string rendered = runtime.Format("{\"type\":\"tool_call\",\"subtype\":\"completed\",\"tool_call\":{\"mcpToolCall\":{\"args\":{\"path\":[\"a\",\"b\"]},\"result\":{\"success\":{\"content\":\"<tool output>\"}}}}}");
                AssertTrue(rendered.StartsWith("[ARMADA:ACTIVITY] tool", StringComparison.Ordinal), "The event is a tool record: " + rendered);
                AssertFalse(rendered.Contains("<tool output>"), "Tool output never reaches the log");
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

            await RunTest("Mux_StreamedTextBecomesWholeLinesAndKeepsSplitMarkers", () =>
            {
                // A `mux print --output-format jsonl` stream: assistant text arrives one token per event, split
                // mid-word, and the result marker is split across three events.
                TestMuxRuntime runtime = new TestMuxRuntime();
                List<string> records = new List<string>();
                foreach (string line in MuxStreamFixture)
                    records.AddRange(runtime.FormatRecords(line));
                records.AddRange(runtime.ExitRecords());

                List<string> text = records.Where(r => !r.StartsWith("[ARMADA:ACTIVITY]", StringComparison.Ordinal)).ToList();
                AssertEqual(
                    "I'll list the directory now.|The directory is empty.|[ARMADA:RESULT] COMPLETE|Nothing to report.",
                    String.Join("|", text),
                    "Each whole line of streamed text is one record");

                int firstTool = records.FindIndex(r => r.StartsWith("[ARMADA:ACTIVITY]", StringComparison.Ordinal));
                AssertTrue(firstTool == 1, "The line before the tool call is written before the tool record: " + String.Join("|", records));
                AssertTrue(records.IndexOf("The directory is empty.") > firstTool, "Text after the tool call follows its record");

                // The tool records are activity signals; the result marker must be detected exactly once.
                List<ProgressParser.ProgressSignal> results = ProgressParser.ParseAll(String.Join("\n", records))
                    .Where(signal => signal.Type == "result").ToList();
                AssertEqual(1, results.Count, "The split marker is detected once");
                AssertEqual("COMPLETE", results[0].Value);
                return Task.CompletedTask;
            });

            await RunTest("Mux_UnfinishedStreamedLineIsWrittenAtExit", () =>
            {
                TestMuxRuntime runtime = new TestMuxRuntime();
                AssertEqual(0, runtime.FormatRecords("{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\"[ARMADA:RESULT] COMP\"}").Length, "An unfinished line is held");
                AssertEqual(0, runtime.FormatRecords("{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\"LETE\"}").Length, "Still no line break");
                string[] exit = runtime.ExitRecords();
                AssertEqual(1, exit.Length, "The held line is written when the process exits");
                AssertEqual("[ARMADA:RESULT] COMPLETE", exit[0]);
                AssertEqual(0, runtime.ExitRecords().Length, "A flushed line is not written twice");
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

            // Defects found by reading 160 real mission logs after the first deployment.
            await RunTest("Claude_SuppressesToolProgressHeartbeat", () =>
            {
                // The CLI emits one of these every 30s while a long command runs. It carries no
                // tool name, argument, or outcome, and one mission logged 22 of them.
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();
                AssertEqual(String.Empty, runtime.Format("{\"type\":\"tool_progress\",\"tool_use_id\":\"tu_1\"}"));
                return Task.CompletedTask;
            });

            await RunTest("Claude_ErroredResult_DoesNotAlsoClaimSuccess", () =>
            {
                // The CLI reports subtype "success" even on an errored turn, which rendered as
                // the self-contradicting "claude result success error".
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();
                AssertEqual(
                    "[ARMADA:ACTIVITY] claude error (1 turns)",
                    runtime.Format("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"num_turns\":1}"));
                AssertEqual(
                    "[ARMADA:ACTIVITY] claude error API Error: 429 rate limited (1 turns)",
                    runtime.Format("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":true,\"num_turns\":1,\"result\":\"API Error: 429 rate limited\"}"),
                    "An errored result carries the CLI's error text");
                AssertEqual(
                    "[ARMADA:ACTIVITY] claude result success (17 turns)",
                    runtime.Format("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":17}"));
                return Task.CompletedTask;
            });

            await RunTest("Redaction_LeavesCamelCaseIdentifiersIntact", () =>
            {
                // A batch of long CamelCase source file names was logged as "<redacted len=40>.cs",
                // which hides what the captain touched and protects nothing.
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();
                string activity = runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"for f in AftOutletSensorExampleFrame4821.cs AirHandlingPerformanceExampleFrame4821.cs; do echo $f; done\"}}]}}");
                AssertContains("AftOutletSensorExampleFrame4821.cs", activity, "A long identifier is not a secret");
                AssertFalse(activity.Contains("<redacted"), "Identifiers must survive redaction intact");
                return Task.CompletedTask;
            });

            await RunTest("Redaction_StillCatchesLabelledCredentials", () =>
            {
                TestClaudeCodeRuntime runtime = new TestClaudeCodeRuntime();

                string header = runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"curl -H 'Authorization: Bearer sk0aBcD1efGh2IjKl3MnOp4QrSt5UvWx'\"}}]}}");
                AssertContains("Bearer <redacted len=", header, "A bearer credential must be redacted, with the scheme left readable");
                AssertFalse(header.Contains("sk0aBcD1efGh2IjKl3MnOp4QrSt5UvWx"), "Raw bearer token must not survive");

                string pair = runtime.Format("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"deploy --api-key=abcdefabcdefabcdefabcdefabcdefab\"}}]}}");
                AssertContains("<redacted len=", pair, "A labelled key must be redacted");
                return Task.CompletedTask;
            });

            await RunTest("SiblingCheckouts_RenderAsParentRelativePaths", () =>
            {
                // Sibling source trees sit next to the dock and mission briefs address them as
                // "../Name". Rendering the absolute form put the same long prefix on hundreds of
                // lines and did not match how the captain was asked to reach them.
                TestOpenCodeRuntime runtime = new TestOpenCodeRuntime();
                runtime.SetDock(_Dock);
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool read ../SiblingSource/output/Decoded/Widget.cs (ok)",
                    runtime.Format("{\"type\":\"tool_use\",\"part\":{\"type\":\"tool\",\"tool\":\"read\",\"state\":{\"status\":\"completed\",\"input\":{\"filePath\":\"/work/fleet/SiblingSource/output/Decoded/Widget.cs\"}}}}"));

                // Anything further away keeps its absolute form; at that distance the location is
                // the information.
                AssertEqual(
                    "[ARMADA:ACTIVITY] tool read /elsewhere/entirely/Other.cs (ok)",
                    runtime.Format("{\"type\":\"tool_use\",\"part\":{\"type\":\"tool\",\"tool\":\"read\",\"state\":{\"status\":\"completed\",\"input\":{\"filePath\":\"/elsewhere/entirely/Other.cs\"}}}}"));
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
                    "[ARMADA:ACTIVITY] claude tool progress",
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

            public string[] FormatRecords(string line)
            {
                return TransformOutputRecords(line).ToArray();
            }

            public string[] ExitRecords()
            {
                return BuildProcessExitRecords().ToArray();
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

            public string[] FormatRecords(string line)
            {
                return TransformOutputRecords(line).ToArray();
            }

            public string[] ExitRecords()
            {
                return BuildProcessExitRecords().ToArray();
            }
        }

        // Recorded from `mux print --output-format jsonl` against a scripted model that streams its reply in
        // pieces and calls one tool; timestamps and run ids are shortened.
        private static readonly string[] MuxStreamFixture = new[]
        {
            "{\"contractVersion\":2,\"eventType\":\"run_started\",\"runId\":\"r1\",\"model\":\"fake\",\"commandName\":\"print\",\"toolsEnabled\":true,\"builtInToolCount\":14,\"effectiveToolCount\":14,\"mcp\":{\"supported\":false,\"configured\":false,\"serverCount\":0}}",
            "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\"I\"}",
            "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\"\\u0027ll\"}",
            "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\" list\"}",
            "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\" the dir\"}",
            "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\"ectory\"}",
            "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\" now\"}",
            "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\".\"}",
            "{\"contractVersion\":2,\"eventType\":\"tool_call_proposed\",\"toolCall\":{\"id\":\"call_1\",\"name\":\"list_directory\",\"arguments\":{\"path\":\".\"}}}",
            "{\"contractVersion\":2,\"eventType\":\"tool_call_approved\",\"toolCallId\":\"call_1\"}",
            "{\"contractVersion\":2,\"eventType\":\"tool_call_completed\",\"toolCallId\":\"call_1\",\"toolName\":\"list_directory\",\"elapsedMs\":2,\"result\":{\"toolCallId\":\"call_1\",\"success\":true,\"content\":\"\"}}",
            "{\"contractVersion\":2,\"eventType\":\"heartbeat\",\"stepNumber\":1}",
            "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\"The dir\"}",
            "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\"ectory is\"}",
            "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\" empty.\\n[ARMADA:RES\"}",
            "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\"ULT] COM\"}",
            "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\"PLETE\\nNo\"}",
            "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\"thing \"}",
            "{\"contractVersion\":2,\"eventType\":\"assistant_text\",\"text\":\"to report.\"}",
            "{\"contractVersion\":2,\"eventType\":\"run_completed\",\"runId\":\"r1\",\"status\":\"completed\",\"iterationsCompleted\":2,\"toolCallCount\":1,\"errorCount\":0,\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"totalTokens\":0,\"estimatedTokens\":4138}}",
        };
    }
}
