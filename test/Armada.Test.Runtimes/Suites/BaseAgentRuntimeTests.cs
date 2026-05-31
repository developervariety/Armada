namespace Armada.Test.Runtimes.Suites
{
    using Armada.Runtimes;
    using Armada.Test.Common;
    using SyslogLogging;

    public class BaseAgentRuntimeTests : TestSuite
    {
        public override string Name => "Base Agent Runtime Tests";

        private LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        protected override async Task RunTestsAsync()
        {
            await RunTest("Constructor Null Logging Throws", () =>
            {
                AssertThrows<ArgumentNullException>(() => new TestAgentRuntime(null!));
            });

            await RunTest("StartAsync Null WorkingDirectory Throws", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                await AssertThrowsAsync<ArgumentNullException>(() => runtime.StartAsync(null!, "prompt"));
            });

            await RunTest("StartAsync Empty WorkingDirectory Throws", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                await AssertThrowsAsync<ArgumentNullException>(() => runtime.StartAsync("", "prompt"));
            });

            await RunTest("StartAsync Null Prompt Throws", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                await AssertThrowsAsync<ArgumentNullException>(() => runtime.StartAsync("/tmp", null!));
            });

            await RunTest("StartAsync Empty Prompt Throws", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                await AssertThrowsAsync<ArgumentNullException>(() => runtime.StartAsync("/tmp", ""));
            });

            await RunTest("IsRunningAsync Invalid ProcessId Returns False", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                bool running = await runtime.IsRunningAsync(99999999);
                AssertFalse(running);
            });

            await RunTest("StopAsync Invalid ProcessId Does Not Throw", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                await runtime.StopAsync(99999999);
            });

            await RunTest("Name Returns Expected", () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                AssertEqual("TestRuntime", runtime.Name);
            });

            await RunTest("SupportsResume Returns False", () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                AssertFalse(runtime.SupportsResume);
            });

            await RunTest("StartAsync Valid Command Returns ProcessId", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                string tempDir = Path.GetTempPath();

                int pid = await runtime.StartAsync(tempDir, "test prompt");
                AssertTrue(pid > 0);

                // Wait briefly for process to finish (dotnet --version exits quickly)
                await Task.Delay(2000);
            });

            await RunTest("StartAsync Without Stdin Redirect Does Not Configure StdinEncoding", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.RedirectStdinOverride = false;
                string tempDir = Path.GetTempPath();

                int pid = await runtime.StartAsync(tempDir, "test prompt");
                AssertTrue(pid > 0);

                await Task.Delay(1000);
            });

            await RunTest("StartAsync Invalid Command Throws", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.CommandOverride = "nonexistent_command_" + Guid.NewGuid().ToString("N");

                string tempDir = Path.GetTempPath();
                bool threw = false;
                try
                {
                    await runtime.StartAsync(tempDir, "test prompt");
                }
                catch
                {
                    threw = true;
                }
                AssertTrue(threw, "Expected exception for invalid command");
            });

            await RunTest("OnOutputReceived Fires For Output", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                string tempDir = Path.GetTempPath();

                List<string> outputLines = new List<string>();
                runtime.OnOutputReceived += (pid, line) =>
                {
                    lock (outputLines)
                    {
                        outputLines.Add(line);
                    }
                };

                int pid = await runtime.StartAsync(tempDir, "test prompt");

                // Wait for process to complete and events to fire
                await Task.Delay(3000);

                AssertTrue(outputLines.Count > 0, "Expected at least one output line");
            });

            await RunTest("OnOutputReceived Preserves Utf8 Stderr Content", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                string tempDir = Path.GetTempPath();
                string expected = "I\u2019m";

                if (OperatingSystem.IsWindows())
                {
                    runtime.CommandOverride = "powershell";
                    runtime.ArgsOverride = new List<string>
                    {
                        "-Command",
                        "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false); " +
                        "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); " +
                        "[Console]::Error.WriteLine('I' + [char]0x2019 + 'm')"
                    };
                }
                else
                {
                    runtime.CommandOverride = "bash";
                    runtime.ArgsOverride = new List<string>
                    {
                        "-lc",
                        "printf 'I\\342\\200\\231m\\n' 1>&2"
                    };
                }

                List<string> outputLines = new List<string>();
                runtime.OnOutputReceived += (pid, line) =>
                {
                    lock (outputLines)
                    {
                        outputLines.Add(line);
                    }
                };

                await runtime.StartAsync(tempDir, "test prompt");
                await Task.Delay(2000);

                AssertTrue(outputLines.Contains(expected), "Expected UTF-8 stderr content to be preserved");
            });

            await RunTest("StartAsync StderrLogWriteDisabled SuppressesFileWriteButKeepsOutputEvent", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.WriteStderrToLogFileOverride = false;
                string tempDir = Path.Combine(Path.GetTempPath(), "armada-runtime-tests-" + Guid.NewGuid().ToString("N"));
                string logFilePath = Path.Combine(tempDir, "agent.log");
                string expected = "ARMADA_STDERR_SENTINEL_" + Guid.NewGuid().ToString("N");

                if (OperatingSystem.IsWindows())
                {
                    runtime.CommandOverride = "powershell";
                    runtime.ArgsOverride = new List<string>
                    {
                        "-Command",
                        "[Console]::Error.WriteLine($env:ARMADA_STDERR_SENTINEL)"
                    };
                }
                else
                {
                    runtime.CommandOverride = "bash";
                    runtime.ArgsOverride = new List<string>
                    {
                        "-lc",
                        "printf '%s\\n' \"$ARMADA_STDERR_SENTINEL\" 1>&2"
                    };
                }

                List<string> outputLines = new List<string>();
                TaskCompletionSource<int?> exited = new TaskCompletionSource<int?>();
                runtime.OnOutputReceived += (pid, line) =>
                {
                    lock (outputLines)
                    {
                        outputLines.Add(line);
                    }
                };
                runtime.OnProcessExited += (pid, code) => exited.TrySetResult(code);

                try
                {
                    Directory.CreateDirectory(tempDir);
                    await runtime.StartAsync(
                        tempDir,
                        "test prompt",
                        new Dictionary<string, string> { ["ARMADA_STDERR_SENTINEL"] = expected },
                        logFilePath);

                    Task completed = await Task.WhenAny(exited.Task, Task.Delay(5000));
                    AssertTrue(completed == exited.Task, "Expected process to exit");

                    string logContent = File.ReadAllText(logFilePath);
                    AssertFalse(logContent.Contains("[stderr] " + expected), "Expected stderr line to be suppressed from log file");
                    AssertFalse(logContent.Contains(expected), "Expected stderr sentinel to be absent from log file");
                    AssertTrue(outputLines.Contains(expected), "Expected stderr to still fire OnOutputReceived");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            });

            await RunTest("StartAsync StderrLogWriteDefaultTrue WritesFile", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                string tempDir = Path.Combine(Path.GetTempPath(), "armada-runtime-tests-" + Guid.NewGuid().ToString("N"));
                string logFilePath = Path.Combine(tempDir, "agent.log");
                string expected = "ARMADA_DEFAULT_STDERR_SENTINEL_" + Guid.NewGuid().ToString("N");

                if (OperatingSystem.IsWindows())
                {
                    runtime.CommandOverride = "powershell";
                    runtime.ArgsOverride = new List<string>
                    {
                        "-Command",
                        "[Console]::Error.WriteLine($env:ARMADA_STDERR_SENTINEL)"
                    };
                }
                else
                {
                    runtime.CommandOverride = "bash";
                    runtime.ArgsOverride = new List<string>
                    {
                        "-lc",
                        "printf '%s\\n' \"$ARMADA_STDERR_SENTINEL\" 1>&2"
                    };
                }

                TaskCompletionSource<int?> exited = new TaskCompletionSource<int?>();
                runtime.OnProcessExited += (pid, code) => exited.TrySetResult(code);

                try
                {
                    Directory.CreateDirectory(tempDir);
                    await runtime.StartAsync(
                        tempDir,
                        "test prompt",
                        new Dictionary<string, string> { ["ARMADA_STDERR_SENTINEL"] = expected },
                        logFilePath);

                    Task completed = await Task.WhenAny(exited.Task, Task.Delay(5000));
                    AssertTrue(completed == exited.Task, "Expected process to exit");

                    string logContent = File.ReadAllText(logFilePath);
                    AssertContains("[stderr] " + expected, logContent, "Expected default stderr log-file write");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            });

            await RunTest("StartAsync StderrLogWriteDisabled EchoesFinalMessageFile", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.WriteStderrToLogFileOverride = false;
                string tempDir = Path.Combine(Path.GetTempPath(), "armada-runtime-tests-" + Guid.NewGuid().ToString("N"));
                string logFilePath = Path.Combine(tempDir, "agent.log");
                string finalMessageFilePath = Path.Combine(tempDir, "final-message.txt");
                string expected = "ARMADA_FINAL_MESSAGE_" + Guid.NewGuid().ToString("N");

                Directory.CreateDirectory(tempDir);
                File.WriteAllText(finalMessageFilePath, expected);

                if (OperatingSystem.IsWindows())
                {
                    runtime.CommandOverride = "powershell";
                    runtime.ArgsOverride = new List<string>
                    {
                        "-Command",
                        "exit 0"
                    };
                }
                else
                {
                    runtime.CommandOverride = "bash";
                    runtime.ArgsOverride = new List<string>
                    {
                        "-lc",
                        "true"
                    };
                }

                TaskCompletionSource<int?> exited = new TaskCompletionSource<int?>();
                runtime.OnProcessExited += (pid, code) => exited.TrySetResult(code);

                try
                {
                    await runtime.StartAsync(
                        tempDir,
                        "test prompt",
                        null,
                        logFilePath,
                        finalMessageFilePath);

                    Task completed = await Task.WhenAny(exited.Task, Task.Delay(5000));
                    AssertTrue(completed == exited.Task, "Expected process to exit");

                    string logContent = File.ReadAllText(logFilePath);
                    AssertContains("=== Final message ===", logContent, "Expected final message header");
                    AssertContains(expected, logContent, "Expected final message to be echoed to log file");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            });

            await RunTest("OnProcessStarted Fires WithPid", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                string tempDir = Path.GetTempPath();

                int startedPid = 0;
                runtime.OnProcessStarted += pid => startedPid = pid;

                int pid = await runtime.StartAsync(tempDir, "test prompt");

                AssertTrue(pid > 0, "Expected a valid PID");
                AssertEqual(pid, startedPid, "OnProcessStarted should fire with the launched process PID");

                await Task.Delay(1000);
            });
        }
    }
}
