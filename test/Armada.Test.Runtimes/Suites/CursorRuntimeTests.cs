namespace Armada.Test.Runtimes.Suites
{
    using System.IO;
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
        }

        // Overrides GetWindowsOfficialInstallPath() to inject a controlled path
        // without touching real system directories.
        private sealed class PathInjectableCursorRuntime : CursorRuntime
        {
            private readonly string? _FakeOfficialPath;

            public PathInjectableCursorRuntime(LoggingModule logging, string? fakeOfficialPath) : base(logging)
            {
                _FakeOfficialPath = fakeOfficialPath;
            }

            public string Command() => GetCommand();

            protected override string? GetWindowsOfficialInstallPath() => _FakeOfficialPath;
        }

        private InspectableCursorRuntime CreateRuntime()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new InspectableCursorRuntime(logging);
        }

        private PathInjectableCursorRuntime CreatePathInjectable(string? fakeOfficialPath)
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return new PathInjectableCursorRuntime(logging, fakeOfficialPath);
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("ExecutablePath Default Is CursorAgent", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                AssertEqual("cursor-agent", runtime.ExecutablePath);
            });

            await RunTest("BuildArguments Uses NonInteractive Text Output", () =>
            {
                InspectableCursorRuntime runtime = CreateRuntime();
                List<string> args = runtime.Args("test prompt");
                AssertEqual("--print", args[0]);
                AssertTrue(args.Contains("--force"));
                AssertTrue(args.Contains("--output-format"));
                AssertTrue(args.Contains("text"));
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

            await RunTest("ValidateReasoningEffort_Xhigh_ReturnsNull", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.Cursor, "xhigh");
                AssertNull(error, "xhigh must be accepted for Cursor");
            });

            await RunTest("ValidateReasoningEffort_Max_ReturnsError", () =>
            {
                string? error = CaptainRuntimeOptions.ValidateReasoningEffort(AgentRuntimeEnum.Cursor, "max");
                AssertNotNull(error, "max is not in the Cursor accepted set (only ClaudeCode accepts max)");
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
                    // Create a fake official install file in a temp directory.
                    string tempDir = Path.Combine(Path.GetTempPath(), "armada_cursor_official_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tempDir);
                    string fakeOfficialPath = Path.Combine(tempDir, "cursor-agent.cmd");
                    File.WriteAllText(fakeOfficialPath, "@echo off");
                    try
                    {
                        PathInjectableCursorRuntime runtime = CreatePathInjectable(fakeOfficialPath);
                        string command = runtime.Command();
                        AssertEqual(fakeOfficialPath, command, "Official Cursor install path must win over any npm shim");
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
                    PathInjectableCursorRuntime runtime = CreatePathInjectable(nonExistentPath);
                    string command = runtime.Command();
                    AssertTrue(command.Contains("cursor-agent", StringComparison.OrdinalIgnoreCase),
                        "When official path does not exist, fallback must still resolve to cursor-agent");
                    AssertFalse(String.Equals(command, nonExistentPath, StringComparison.OrdinalIgnoreCase),
                        "Non-existent official path must not be returned");
                });
            }
        }
    }
}
