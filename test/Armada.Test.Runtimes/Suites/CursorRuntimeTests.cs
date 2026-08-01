namespace Armada.Test.Runtimes.Suites
{
    using System.IO;
    using Armada.Core.Models;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    public class CursorRuntimeTests : TestSuite
    {
        public override string Name => "Cursor Runtime Tests";

        private sealed class InspectableCursorRuntime : CursorRuntime
        {
            public InspectableCursorRuntime(LoggingModule logging) : base(logging)
            {
            }

            public string Command() => GetCommand();

            public List<string> Args(string prompt, string? model = null, string? finalMessageFilePath = null) =>
                BuildArguments(Path.GetTempPath(), prompt, model, finalMessageFilePath, null);

            public bool StdinEnabled() => UsePromptStdin;

            public void FeedUsage(int processId, string line) => HandleRawOutputLine(processId, line);

            public string TransformLine(string line) => TransformOutputLine(line);

            public void SetDock(string directory) => WorkingDirectory = directory;
        }

        // Overrides GetWindowsOfficialInstallPath() to inject a controlled path
        // without touching real system directories.
        private sealed class PathInjectableCursorRuntime : CursorRuntime
        {
            private readonly string? _FakeOfficialPath;
            private readonly string? _FakeResolvedPath;

            public PathInjectableCursorRuntime(
                LoggingModule logging,
                string? fakeOfficialPath,
                string? fakeResolvedPath) : base(logging)
            {
                _FakeOfficialPath = fakeOfficialPath;
                _FakeResolvedPath = fakeResolvedPath;
            }

            public int ResolveCallCount { get; private set; }

            public string Command() => GetCommand();

            protected override string? GetWindowsOfficialInstallPath() => _FakeOfficialPath;

            protected override string ResolveConfiguredExecutable(string executablePath)
            {
                ResolveCallCount++;
                if (!String.IsNullOrEmpty(_FakeResolvedPath))
                    return _FakeResolvedPath;
                return base.ResolveConfiguredExecutable(executablePath);
            }
        }

        private InspectableCursorRuntime CreateRuntime()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InspectableCursorRuntime(logging);
        }

        private PathInjectableCursorRuntime CreatePathInjectable(string? fakeOfficialPath, string? fakeResolvedPath = null)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new PathInjectableCursorRuntime(logging, fakeOfficialPath, fakeResolvedPath);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("ExecutablePath Default Is CursorAgent", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                AssertEqual("cursor-agent", runtime.ExecutablePath);
            });

            await RunTest("BuildArguments Uses NonInteractiveStructuredOutput", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                AssertEqual("--print", args[0]);
                AssertTrue(args.Contains("--force"));
                AssertTrue(args.Contains("--output-format"));
                AssertTrue(args.Contains("stream-json"));
            });

            await RunTest("ResultPublishesExactUsage", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                RuntimeTokenUsage? captured = null;
                runtime.OnTokenUsageReceived += (_, usage) => captured = usage;
                runtime.FeedUsage(11, "{\"type\":\"result\",\"usage\":{\"inputTokens\":11010,\"outputTokens\":23,\"cacheReadTokens\":2905,\"cacheWriteTokens\":0}}");
                AssertNotNull(captured);
                AssertEqual(11010L, captured!.InputTokens);
                AssertEqual(23L, captured.OutputTokens);
                AssertEqual(2905L, captured.CacheReadTokens);
            });

            await RunTest("BuildArguments Includes Model When Supplied", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt", "gpt-5");
                int modelIndex = args.IndexOf("--model");
                AssertTrue(modelIndex >= 0);
                AssertEqual("gpt-5", args[modelIndex + 1]);
            });

            await RunTest("Command Uses CursorAgent", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                string command = runtime.Command();
                AssertTrue(command.Contains("cursor-agent", StringComparison.OrdinalIgnoreCase), "Expected cursor-agent command");
            });

            await RunTest("UsePromptStdin Is True", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                AssertTrue(runtime.StdinEnabled(), "Cursor runtime must use stdin to avoid Windows cmd.exe length limit");
            });

            await RunTest("BuildArguments_LongPrompt_PromptNotInArguments", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                string longPrompt = new string('x', 16384);
                List<string> args = runtime.Args(longPrompt);
                foreach (string arg in args)
                {
                    AssertFalse(arg.Length > 1000, "No single argument should contain the long prompt; prompt must be sent via stdin");
                }
                AssertFalse(args.Contains(longPrompt), "Long prompt must not appear as a CLI argument");
            });

            await RunTest("UsePromptStdin_DeliversRolePreambleViaStdin", () =>
            {
                string rolePreamble = "Role: You are an Armada worker agent.";
                string prompt = rolePreamble + " Mission: test objective. Branch: main.";
                InspectableCursorRuntime runtime = CreateRuntime();
                AssertTrue(runtime.StdinEnabled(), "Cursor runtime must deliver the prompt via stdin");
                List<string> args = runtime.Args(prompt);
                AssertFalse(args.Contains(prompt), "Cursor prompt must not appear in CLI arguments; it is delivered via stdin");
                AssertFalse(args.Any(arg => arg.Contains(rolePreamble)), "Cursor CLI arguments must not contain the role preamble; it is delivered via stdin");
            });

            // Pinning tests for Cursor reasoningEffort validation.
            // cursor-agent CLI v2026.04.29-c83a488 does not expose a --thinking-effort /
            // --reasoning-effort flag; these tests pin accept/reject behavior so that wiring
            // the flag forward becomes a safe, mechanical step when cursor-agent gains it.

            await RunTest("ValidateReasoningEffort_Null_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.Cursor, null);
                AssertNull(error, "Null reasoningEffort must be accepted (use cursor-agent default)");
            });

            await RunTest("ValidateReasoningEffort_Low_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.Cursor, "low");
                AssertNull(error, "low must be accepted for Cursor");
            });

            await RunTest("ValidateReasoningEffort_Medium_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.Cursor, "medium");
                AssertNull(error, "medium must be accepted for Cursor");
            });

            await RunTest("ValidateReasoningEffort_High_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.Cursor, "high");
                AssertNull(error, "high must be accepted for Cursor");
            });

            await RunTest("ValidateReasoningEffort_Xhigh_ReturnsError", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.Cursor, "xhigh");
                AssertNotNull(error, "xhigh must be rejected for Cursor");
                AssertContains("Accepted values: low, medium, high.", error!, "Error should list the supported values");
            });

            await RunTest("ValidateReasoningEffort_Max_ReturnsError", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.Cursor, "max");
                AssertNotNull(error, "max must be rejected for Cursor");
                AssertContains("Accepted values: low, medium, high.", error!, "Error should list the supported values");
            });

            await RunTest("ValidateReasoningEffort_InvalidValue_ReturnsError", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.Cursor, "ultra");
                AssertNotNull(error, "Unrecognised value must be rejected for Cursor");
            });

            await RunTest("ValidateReasoningEffort_CaseInsensitive_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.Cursor, "HIGH");
                AssertNull(error, "Validation must be case-insensitive");
            });

            // Command resolution preference tests.

            await RunTest("GetCommand_TestEnvVarOverride_UsesOverridePath", () =>
            {
                string fakePath = Path.Combine(Path.GetTempPath(), "fake-cursor-agent.cmd");
                try
                {
                    Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT", fakePath);
                    InspectableCursorRuntime runtime = CreateRuntime();
                    string command = runtime.Command();
                    AssertEqual(fakePath, command, "ARMADA_TEST_CURSOR_AGENT must take priority over all other resolution");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("ARMADA_TEST_CURSOR_AGENT", null);
                }
            });

            if (OperatingSystem.IsWindows())
            {
                await RunTest("GetCommand_OfficialPathExists_TakesPriorityOverNpmShim", () =>
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_cursor_official_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    string fakeOfficialPath = Path.Combine(tempDir, "cursor-agent.cmd");
                    File.WriteAllText(fakeOfficialPath, "@echo off");
                    string fakeNpmDir = Path.Combine(tempDir, "npm");
                    Directory.CreateDirectory(fakeNpmDir);
                    string fakeNpmShimPath = Path.Combine(fakeNpmDir, "cursor-agent.cmd");
                    File.WriteAllText(fakeNpmShimPath, "@echo off\r\necho stale shim\r\n");
                    try
                    {
                        PathInjectableCursorRuntime runtime = CreatePathInjectable(fakeOfficialPath, fakeNpmShimPath);
                        string command = runtime.Command();
                        AssertEqual(fakeOfficialPath, command, "Official Cursor install path must win over any npm shim");
                        AssertEqual(0, runtime.ResolveCallCount, "Fallback resolution must not run when official install exists");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                });

                await RunTest("GetCommand_OfficialPathMissing_FallsBackToResolution", () =>
                {
                    // Return a path that does not exist -- runtime must fall back to ResolveExecutable.
                    string nonExistentPath = Path.Combine(Path.GetTempPath(), "armada_no_cursor_" + Guid.NewGuid().ToString("N"), "cursor-agent.cmd");
                    string fakeResolvedPath = Path.Combine(Path.GetTempPath(), "armada_cursor_resolved_" + Guid.NewGuid().ToString("N"), "cursor-agent.cmd");
                    PathInjectableCursorRuntime runtime = CreatePathInjectable(nonExistentPath, fakeResolvedPath);
                    string command = runtime.Command();
                    AssertEqual(fakeResolvedPath, command,
                        "When official path does not exist, fallback must still resolve to cursor-agent");
                    AssertEqual(1, runtime.ResolveCallCount, "Missing official path must fall back exactly once");
                });

                await RunTest("GetCommand_CustomExecutablePath_DoesNotUseOfficialPath", () =>
                {
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_cursor_custom_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    string fakeOfficialPath = Path.Combine(tempDir, "cursor-agent.cmd");
                    File.WriteAllText(fakeOfficialPath, "@echo off");
                    try
                    {
                        string fakeResolvedPath = Path.Combine(tempDir, "custom-cursor-agent.cmd");
                        PathInjectableCursorRuntime runtime = CreatePathInjectable(fakeOfficialPath, fakeResolvedPath);
                        runtime.ExecutablePath = "custom-cursor-agent";
                        string command = runtime.Command();
                        AssertEqual(fakeResolvedPath, command, "Custom executable paths must keep using configured resolution");
                        AssertEqual(1, runtime.ResolveCallCount, "Custom executable path must bypass official default lookup");
                    }
                    finally
                    {
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                });
            }

            // --- Real cursor-agent capture (v2026.07.23-e383d2b, --output-format stream-json) ---
            //
            // Captured from a headless cursor-agent run against a scratch directory; identifiers
            // and paths are replaced with placeholders. Pinned verbatim because the shape is the whole reason Cursor tool calls
            // were invisible: the tool is named by the KEY of the tool_call payload, and the map
            // also holds a string ("toolCallId"), an array ("hookAdditionalContexts"), and the
            // full tool OUTPUT, none of which may reach the mission log.

            await RunTest("RealJsonl_ReadToolCall_RendersRelativePathAndStatus", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                runtime.SetDock("/work/example-dock");

                string line = "{\"type\":\"tool_call\",\"subtype\":\"completed\",\"call_id\":\"call-example-0\",\"tool_call\":{\"readToolCall\":{\"args\":{\"path\":\"/work/example-dock/sample.txt\"},\"result\":{\"success\":{\"content\":\"hello world\\n\",\"isEmpty\":false,\"exceededLimit\":false,\"totalLines\":2,\"fileSize\":12,\"path\":\"/work/example-dock/sample.txt\",\"readRange\":{\"startLine\":1,\"endLine\":2},\"relatedCursorRulePaths\":[],\"relatedCursorRules\":[]}}},\"hookAdditionalContexts\":[],\"toolCallId\":\"call-example-0\",\"startedAtMs\":\"1785547476498\",\"completedAtMs\":\"1785547476528\"},\"model_call_id\":\"model-call-example\",\"session_id\":\"session-example\",\"timestamp_ms\":1785547476534}";

                string result = runtime.TransformLine(line);
                AssertEqual("[ARMADA:ACTIVITY] tool read sample.txt (ok)", result,
                    "A real read call must render with a canonical name, a dock-relative path, and a status");
                AssertFalse(result.Contains("hello world"), "File contents must never reach the mission log");
                AssertFalse(result.Contains("\"type\""), "The raw event must not leak into the mission log");
            });

            await RunTest("RealJsonl_ShellToolCall_RendersCommandWithoutOutput", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();

                string line = "{\"type\":\"tool_call\",\"subtype\":\"completed\",\"call_id\":\"call-example-1\",\"tool_call\":{\"shellToolCall\":{\"args\":{\"command\":\"ls -la\",\"workingDirectory\":\"\",\"timeout\":30000,\"toolCallId\":\"call-example-1\",\"simpleCommands\":[\"ls\"],\"hasInputRedirect\":false,\"hasOutputRedirect\":false,\"parsingResult\":{\"parsingFailed\":false,\"executableCommands\":[{\"name\":\"ls\",\"args\":[{\"type\":\"word\",\"value\":\"-la\"}],\"fullText\":\"ls -la\"}],\"hasRedirects\":false,\"hasCommandSubstitution\":false,\"redirects\":[]},\"fileOutputThresholdBytes\":\"40000\",\"isBackground\":false,\"skipApproval\":false,\"timeoutBehavior\":\"TIMEOUT_BEHAVIOR_BACKGROUND\",\"hardTimeout\":86400000,\"description\":\"List directory contents in detail\",\"closeStdin\":true,\"conversationId\":\"session-example\"},\"result\":{\"success\":{\"command\":\"ls -la\",\"workingDirectory\":\"\",\"exitCode\":0,\"signal\":\"\",\"stdout\":\"total 16\\ndrwx------ 2 ubuntu ubuntu 4096 Aug  1 01:24 .\\ndrwxrwxrwt 1 root   root   4096 Aug  1 01:24 ..\\n-rw-r--r-- 1 ubuntu ubuntu   12 Aug  1 01:24 sample.txt\\n\",\"stderr\":\"\",\"executionTime\":58,\"interleavedOutput\":\"total 16\\ndrwx------ 2 ubuntu ubuntu 4096 Aug  1 01:24 .\\ndrwxrwxrwt 1 root   root   4096 Aug  1 01:24 ..\\n-rw-r--r-- 1 ubuntu ubuntu   12 Aug  1 01:24 sample.txt\\n\",\"localExecutionTimeMs\":22},\"isBackground\":false},\"description\":\"List directory contents in detail\"},\"hookAdditionalContexts\":[],\"toolCallId\":\"call-example-1\",\"startedAtMs\":\"1785547476502\",\"completedAtMs\":\"1785547476560\"},\"model_call_id\":\"model-call-example\",\"session_id\":\"session-example\",\"timestamp_ms\":1785547476568}";

                string result = runtime.TransformLine(line);
                AssertEqual("[ARMADA:ACTIVITY] tool bash ls -la (ok)", result,
                    "shellToolCall must collapse to the canonical bash verb");
                AssertFalse(result.Contains("drwx"), "Command output must never reach the mission log");
                AssertFalse(result.Contains("TIMEOUT_BEHAVIOR"), "Tool plumbing arguments must not reach the mission log");
            });

            await RunTest("RealJsonl_StartedAndThinkingEvents_AreSuppressed", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();

                string started = "{\"type\":\"tool_call\",\"subtype\":\"started\",\"call_id\":\"c0\",\"tool_call\":{\"readToolCall\":{\"args\":{\"path\":\"/tmp/x/sample.txt\"}},\"hookAdditionalContexts\":[],\"toolCallId\":\"c0\",\"startedAtMs\":\"1785547476498\"}}";
                AssertEqual(String.Empty, runtime.TransformLine(started),
                    "The started event duplicates the completed one and carries no outcome");

                string thinking = "{\"type\":\"thinking\",\"subtype\":\"delta\",\"text\":\"Reading sample.txt and\",\"session_id\":\"s\",\"timestamp_ms\":1785547476510}";
                AssertEqual(String.Empty, runtime.TransformLine(thinking),
                    "Reasoning is dropped by every runtime");

                string init = "{\"type\":\"system\",\"subtype\":\"init\",\"apiKeySource\":\"login\",\"cwd\":\"/tmp/x\",\"model\":\"Cursor Grok 4.5 High Fast\"}";
                AssertEqual(String.Empty, runtime.TransformLine(init), "Session bookkeeping must not be logged");

                string assistant = "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"I'll read sample.txt.\"}]},\"session_id\":\"s\"}";
                AssertEqual("I'll read sample.txt.", runtime.TransformLine(assistant),
                    "Assistant narration is the one thing Cursor logs today and must survive");
            });
        }
    }
}
