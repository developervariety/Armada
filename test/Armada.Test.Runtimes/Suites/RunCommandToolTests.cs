namespace Armada.Test.Runtimes.Suites
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Runtimes;
    using Armada.Runtimes.Tools;
    using Armada.Test.Common;

    /// <summary>
    /// The command tool an API-endpoint mission uses for git, builds and tests. Each case pins one guarantee
    /// the tool makes: where it runs, what environment it gets, how it ends, and how much it returns.
    /// </summary>
    public class RunCommandToolTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Run Command Tool";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A command runs in the workspace and returns its exit code and output", async () =>
            {
                string workspace = NewWorkspace();
                try
                {
                    RunResult result = await RunAsync(workspace, new { command = "pwd && echo hello" }).ConfigureAwait(false);
                    AssertTrue(result.Success, "a zero exit is a successful call");
                    AssertEqual(0, result.ExitCode ?? -1);
                    AssertContains(Path.GetFileName(workspace), result.Output, "it runs in the workspace");
                    AssertContains("hello", result.Output);
                }
                finally { Directory.Delete(workspace, true); }
            });

            await RunTest("A non-zero exit is reported with its code and output, not swallowed", async () =>
            {
                string workspace = NewWorkspace();
                try
                {
                    // A failing test suite is a legitimate RESULT, so the output must come back whole.
                    RunResult result = await RunAsync(workspace, new { command = "echo 3 tests failed; exit 3" }).ConfigureAwait(false);
                    AssertFalse(result.Success, "a non-zero exit reads as a failed call");
                    AssertEqual("nonzero_exit", result.Error);
                    AssertEqual(3, result.ExitCode ?? -1);
                    AssertContains("3 tests failed", result.Output, "the output of a failing command is kept");
                }
                finally { Directory.Delete(workspace, true); }
            });

            await RunTest("A working directory outside the workspace is refused", async () =>
            {
                string workspace = NewWorkspace();
                try
                {
                    foreach (string escape in new[] { "..", "../..", "/", "/etc", Path.GetTempPath() })
                    {
                        bool refused = false;
                        try
                        {
                            await RunAsync(workspace, new { command = "echo escaped", working_directory = escape }).ConfigureAwait(false);
                        }
                        catch (WorkspaceBoundaryException)
                        {
                            refused = true;
                        }

                        AssertTrue(refused, "working_directory '" + escape + "' must be refused");
                    }

                    // The agent loop reports that exception under its own class, so the refusal carries a reason.
                    AssertEqual("boundary_refused", ApiAgentRuntime.ClassifyToolException(new WorkspaceBoundaryException()));
                }
                finally { Directory.Delete(workspace, true); }
            });

            await RunTest("A subdirectory inside the workspace is accepted", async () =>
            {
                string workspace = NewWorkspace();
                try
                {
                    Directory.CreateDirectory(Path.Combine(workspace, "src", "lib"));
                    RunResult result = await RunAsync(workspace, new { command = "pwd", working_directory = "src/lib" }).ConfigureAwait(false);
                    AssertTrue(result.Success);
                    AssertContains(Path.Combine("src", "lib").Replace('\\', '/'), result.Output.Replace('\\', '/'));
                }
                finally { Directory.Delete(workspace, true); }
            });

            await RunTest("No admiral or provider credential reaches the command through its environment", async () =>
            {
                // The allowlist decides; a secret-looking name is dropped whatever its value.
                Hashtable source = new Hashtable
                {
                    ["PATH"] = "/usr/bin:/bin",
                    ["HOME"] = "/home/example",
                    ["ANTHROPIC_API_KEY"] = "sk-example",
                    ["ANTHROPIC_AUTH_TOKEN"] = "token-example",
                    ["OPENAI_API_KEY"] = "sk-example",
                    ["ARMADA_VILAO_KEY"] = "provider-key-example",
                    ["ARMADA_API_KEY"] = "admiral-key-example",
                    ["ARMADA_TYPESAFE_KEY"] = "classifier-key-example",
                    ["SOME_NEW_SECRET"] = "unknown-example"
                };
                Dictionary<string, string?> target = new Dictionary<string, string?> { ["LEFTOVER"] = "must be cleared" };
                RunCommandTool.ApplyEnvironment(target, source);

                AssertEqual("/usr/bin:/bin", target["PATH"]);
                AssertEqual("/home/example", target["HOME"]);
                foreach (string name in target.Keys)
                {
                    AssertFalse(name.Contains("KEY", StringComparison.Ordinal) || name.Contains("TOKEN", StringComparison.Ordinal) || name.Contains("SECRET", StringComparison.Ordinal),
                        name + " must not reach the command");
                }

                AssertFalse(target.ContainsKey("LEFTOVER"), "the start environment is cleared before the allowlist is applied");
                AssertEqual("0", target["GIT_TERMINAL_PROMPT"], "git must not prompt");
            });

            await RunTest("A variable set in the admiral process is invisible to the command", async () =>
            {
                // End to end, not only the helper: set a canary in this process and ask the command for it.
                const string canaryName = "ARMADA_RUN_COMMAND_CANARY_KEY";
                string workspace = NewWorkspace();
                Environment.SetEnvironmentVariable(canaryName, "leaked");
                try
                {
                    RunResult result = await RunAsync(workspace, new { command = "env" }).ConfigureAwait(false);
                    AssertTrue(result.Success);
                    AssertFalse(result.Output.Contains(canaryName, StringComparison.Ordinal), "the canary must not appear in the command's environment");
                    AssertFalse(result.Output.Contains("leaked", StringComparison.Ordinal));
                }
                finally
                {
                    Environment.SetEnvironmentVariable(canaryName, null);
                    Directory.Delete(workspace, true);
                }
            });

            await RunTest("Standard input is closed, so a prompting command ends instead of hanging", async () =>
            {
                string workspace = NewWorkspace();
                try
                {
                    Stopwatch clock = Stopwatch.StartNew();
                    RunResult result = await RunAsync(workspace, new { command = "read -r answer || echo got-eof", timeout_seconds = 30 }).ConfigureAwait(false);
                    clock.Stop();
                    AssertContains("got-eof", result.Output, "read sees end of input");
                    AssertFalse(result.TimedOut, "it must not wait for the timeout");
                    AssertTrue(clock.Elapsed < TimeSpan.FromSeconds(20), "it returns promptly");
                }
                finally { Directory.Delete(workspace, true); }
            });

            await RunTest("A timeout kills the command and everything it started", async () =>
            {
                string workspace = NewWorkspace();
                try
                {
                    // The background child is the point: killing only bash would leave it running.
                    Stopwatch clock = Stopwatch.StartNew();
                    RunResult result = await RunAsync(workspace, new { command = "sleep 300 & echo $! > child.pid; wait", timeout_seconds = 2 }).ConfigureAwait(false);
                    clock.Stop();

                    AssertTrue(result.TimedOut, "the command times out");
                    AssertEqual("timed_out", result.Error);
                    AssertNull(result.ExitCode, "a killed command has no exit code to report");
                    AssertTrue(clock.Elapsed < TimeSpan.FromSeconds(60), "the call returns soon after the timeout");

                    int childPid = Int32.Parse(File.ReadAllText(Path.Combine(workspace, "child.pid")).Trim());
                    AssertTrue(WaitForExit(childPid, TimeSpan.FromSeconds(10)), "the background child " + childPid + " must be killed with its parent");
                }
                finally { Directory.Delete(workspace, true); }
            });

            await RunTest("A timeout kills a background child the shell left behind", async () =>
            {
                string workspace = NewWorkspace();
                try
                {
                    // bash exits at once; the child keeps the output pipe open and outlives it.
                    RunResult result = await RunAsync(workspace, new { command = "sleep 30 & echo $! > child.pid; echo parent-done", timeout_seconds = 1 }).ConfigureAwait(false);
                    AssertTrue(result.TimedOut, "the open output pipe holds the call until the timeout");
                    AssertContains("parent-done", result.Output, "the output written before the timeout is kept");

                    int childPid = Int32.Parse(File.ReadAllText(Path.Combine(workspace, "child.pid")).Trim());
                    bool exited = WaitForExit(childPid, TimeSpan.FromSeconds(10));
                    if (!exited) TryKill(childPid);
                    AssertTrue(exited, "the child " + childPid + " must not survive its exited shell");
                }
                finally { Directory.Delete(workspace, true); }
            });

            await RunTest("Cancellation kills a background child the shell left behind", async () =>
            {
                string workspace = NewWorkspace();
                try
                {
                    using (CancellationTokenSource cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                    {
                        RunCommandTool tool = new RunCommandTool();
                        string arguments = JsonSerializer.Serialize(new { command = "sleep 30 & echo $! > child.pid; echo parent-done", timeout_seconds = 60 });
                        ToolResult toolResult = await tool.ExecuteAsync("call_cancel", arguments, workspace, cancel.Token).ConfigureAwait(false);
                        AssertFalse(toolResult.Success, "a cancelled command is not a success");
                        AssertContains("cancelled", toolResult.Content, "the result names the cancellation");
                    }

                    int childPid = Int32.Parse(File.ReadAllText(Path.Combine(workspace, "child.pid")).Trim());
                    bool exited = WaitForExit(childPid, TimeSpan.FromSeconds(10));
                    if (!exited) TryKill(childPid);
                    AssertTrue(exited, "the child " + childPid + " must not survive a cancelled call");
                }
                finally { Directory.Delete(workspace, true); }
            });

            await RunTest("A long line without a newline is capped while it is read", async () =>
            {
                string workspace = NewWorkspace();
                try
                {
                    // 64 MiB on one line. A line reader holds all of it (twice, as UTF-16) before any cap applies.
                    const long streamBytes = 64L * 1024 * 1024;
                    long allocatedBefore = GC.GetTotalAllocatedBytes(true);
                    RunResult result = await RunAsync(workspace, new
                    {
                        command = "head -c " + streamBytes + " /dev/zero | tr '\\0' x; echo; echo TAIL-MARK",
                        timeout_seconds = 120
                    }).ConfigureAwait(false);
                    long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;

                    AssertTrue(result.Success, "the command completes");
                    AssertTrue(result.Truncated, "the output is truncated");
                    AssertTrue(result.OmittedBytes > streamBytes - ToolSafetyLimits.MaxProcessOutputBytes, "the omitted bytes are counted (" + result.OmittedBytes + ")");
                    AssertContains("TAIL-MARK", result.Output, "the end is kept");
                    int keptBytes = Encoding.UTF8.GetByteCount(result.Output);
                    AssertTrue(keptBytes < ToolSafetyLimits.MaxProcessOutputBytes + 512, "the kept output stays within the cap (" + keptBytes + " bytes)");
                    AssertTrue(allocated < streamBytes / 2, "memory stays bounded while reading (" + allocated + " bytes allocated for a " + streamBytes + "-byte line)");
                }
                finally { Directory.Delete(workspace, true); }
            });

            await RunTest("Output above the cap keeps its beginning and its end", async () =>
            {
                string workspace = NewWorkspace();
                try
                {
                    // Far above the cap. A test runner prints its totals LAST, so the tail must survive.
                    RunResult result = await RunAsync(workspace, new { command = "seq 1 200000; echo FINAL-SUMMARY-LINE" }).ConfigureAwait(false);
                    AssertTrue(result.Success);
                    AssertTrue(result.Truncated, "the output is truncated");
                    AssertTrue(result.OmittedBytes > 0, "the omitted byte count is reported");
                    AssertTrue(result.Output.StartsWith("1\n2\n3\n", StringComparison.Ordinal), "the beginning is kept");
                    AssertContains("FINAL-SUMMARY-LINE", result.Output, "the end is kept");
                    AssertContains("bytes of output omitted", result.Output, "the cut is marked");

                    int keptBytes = Encoding.UTF8.GetByteCount(result.Output);
                    AssertTrue(keptBytes < ToolSafetyLimits.MaxProcessOutputBytes + 512, "the kept output stays within the cap (" + keptBytes + " bytes)");
                }
                finally { Directory.Delete(workspace, true); }
            });

            await RunTest("Progress-only output past the token floor is pruned and archived", async () =>
            {
                string workspace = NewWorkspace();
                try
                {
                    // Well above 10k estimated tokens of download lines, with the assertion last.
                    RunResult result = await RunAsync(workspace, new
                    {
                        command = "for i in $(seq 0 2499); do echo \"Downloading package $i\"; done; echo \"FAILED: Assert.Equal expected 4 actual 5\"; echo \"Failed: 1\""
                    }).ConfigureAwait(false);
                    AssertTrue(result.Pruned, "progress past the floor is pruned");
                    AssertFalse(String.IsNullOrEmpty(result.OutputArchive), "the full output is archived");
                    AssertTrue(File.Exists(Path.Combine(workspace, result.OutputArchive!.Replace('/', Path.DirectorySeparatorChar))),
                        "the archive is in the workspace");
                    AssertContains("FAILED: Assert.Equal", result.Output, "the assertion stays in the returned output");
                    AssertContains("progress omitted", result.Output, "the cut is marked");
                    string archived = File.ReadAllText(Path.Combine(workspace, result.OutputArchive.Replace('/', Path.DirectorySeparatorChar)));
                    AssertContains("Downloading package 2000", archived, "the archive holds the dropped progress");
                }
                finally { Directory.Delete(workspace, true); }
            });

            await RunTest("A missing command is refused with a reason", async () =>
            {
                string workspace = NewWorkspace();
                try
                {
                    RunResult result = await RunAsync(workspace, new { command = "   " }).ConfigureAwait(false);
                    AssertFalse(result.Success);
                    AssertEqual("missing_parameter", result.Error);
                }
                finally { Directory.Delete(workspace, true); }
            });

            await RunTest("A run_command activity line reads like a CLI harness's shell line", () =>
            {
                // An operator reading a mission log must see WHICH command ran, and see it under the same
                // canonical verb every CLI runtime's shell tool renders as, so two missions compare directly.
                string rendered = StructuredRuntimeLogFormatter.BuildToolActivity(
                    "run_command", "git diff --stat origin/main...HEAD", StructuredRuntimeLogFormatter.OkStatus);
                AssertContains("tool bash ", rendered, "run_command renders as the canonical bash verb");
                AssertContains("git diff --stat origin/main...HEAD", rendered, "the command itself is on the line");
                AssertTrue(rendered.EndsWith("(ok)", StringComparison.Ordinal));
                return Task.CompletedTask;
            });

            await RunTest("The command tool is registered only when a run opts in", () =>
            {
                BuiltInToolRegistry defaultRegistry = new BuiltInToolRegistry();
                AssertFalse(defaultRegistry.HasTool("run_command"), "the default registry has no command tool");

                BuiltInToolRegistry missionRegistry = new BuiltInToolRegistry(null, includeCommandTool: true);
                AssertTrue(missionRegistry.HasTool("run_command"), "an opted-in registry has it");
                AssertEqual(ToolMutationKind.Mutating, missionRegistry.GetMutationKind("run_command"), "it is declared mutating");
                return Task.CompletedTask;
            });
        }

        private static string NewWorkspace()
        {
            string path = Path.Combine(Path.GetTempPath(), "armada_run_command_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static async Task<RunResult> RunAsync(string workspace, object arguments)
        {
            RunCommandTool tool = new RunCommandTool();
            ToolResult toolResult = await tool.ExecuteAsync("call_test", JsonSerializer.Serialize(arguments), workspace, CancellationToken.None).ConfigureAwait(false);
            RunResult parsed = JsonSerializer.Deserialize<RunResult>(toolResult.Content) ?? new RunResult();
            parsed.Success = toolResult.Success;
            return parsed;
        }

        private static bool WaitForExit(int pid, TimeSpan within)
        {
            DateTime deadline = DateTime.UtcNow + within;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using (Process process = Process.GetProcessById(pid))
                    {
                        if (process.HasExited) return true;
                    }
                }
                catch (ArgumentException)
                {
                    return true;
                }

                Thread.Sleep(200);
            }

            return false;
        }

        private static void TryKill(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    process.Kill();
                }
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        private sealed class RunResult
        {
            [System.Text.Json.Serialization.JsonIgnore]
            public bool Success { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("error")]
            public string? Error { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("exit_code")]
            public int? ExitCode { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("timed_out")]
            public bool TimedOut { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("truncated")]
            public bool Truncated { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("pruned")]
            public bool Pruned { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("omitted_bytes")]
            public long OmittedBytes { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("output_archive")]
            public string? OutputArchive { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("output")]
            public string Output { get; set; } = String.Empty;
        }
    }
}
