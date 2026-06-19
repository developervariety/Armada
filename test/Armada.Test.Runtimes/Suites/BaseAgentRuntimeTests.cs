namespace Armada.Test.Runtimes.Suites
{
    using System.Diagnostics;
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

        // Emits `sentinel` on stderr WITHOUT the full sentinel string appearing in the
        // command arguments. StartAsync echoes the joined args into the log-file header,
        // so a literal sentinel in the args would show up regardless of stderr gating.
        // Splitting it and concatenating in the shell keeps the joined string off the args.
        private void ConfigureStderrEmitter(TestAgentRuntime runtime, string sentinel)
        {
            int mid = sentinel.Length / 2;
            string left = sentinel.Substring(0, mid);
            string right = sentinel.Substring(mid);

            if (OperatingSystem.IsWindows())
            {
                runtime.CommandOverride = "powershell";
                runtime.ArgsOverride = new List<string>
                {
                    "-Command",
                    "[Console]::Error.WriteLine('" + left + "' + '" + right + "')"
                };
            }
            else
            {
                runtime.CommandOverride = "bash";
                runtime.ArgsOverride = new List<string>
                {
                    "-lc",
                    "printf '%s%s\\n' '" + left + "' '" + right + "' 1>&2"
                };
            }
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

            await RunTest("StartAsync_ProcessStartInfo_SetsMsBuildNoNodeReuseEnvironment", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.CaptureStartInfoAndThrow = true;
                Dictionary<string, string> environment = new Dictionary<string, string>
                {
                    ["MSBUILDDISABLENODEREUSE"] = "caller-value",
                    ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "caller-value",
                    ["ARMADA_TEST_CALLER_ENVIRONMENT"] = "caller-preserved"
                };

                await AssertThrowsAsync<InvalidOperationException>(() =>
                    runtime.StartAsync(Path.GetTempPath(), "test prompt", environment: environment));

                AssertTrue(runtime.CapturedStartInfo != null, "Expected StartAsync to expose ProcessStartInfo before launch");
                ProcessStartInfo startInfo = runtime.CapturedStartInfo!;

                AssertEqual("1", startInfo.Environment["MSBUILDDISABLENODEREUSE"]);
                AssertEqual("0", startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
                AssertEqual("caller-preserved", startInfo.Environment["ARMADA_TEST_CALLER_ENVIRONMENT"]);
                AssertEqual("1", startInfo.Environment["TEST_AGENT_RUNTIME_ENVIRONMENT_APPLIED"]);
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

            await RunTest("WriteStderrToLogFile False Suppresses Log File But Preserves OnOutputReceived", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.WriteStderrToLogFileOverride = false;
                string tempDir = Path.GetTempPath();
                string sentinel = "STDERR_SENTINEL_" + Guid.NewGuid().ToString("N");
                ConfigureStderrEmitter(runtime, sentinel);

                string logFilePath = Path.Combine(Path.GetTempPath(), "armada_stderr_gate_" + Guid.NewGuid().ToString("N") + ".log");

                List<string> outputLines = new List<string>();
                runtime.OnOutputReceived += (pid, line) =>
                {
                    lock (outputLines) { outputLines.Add(line); }
                };

                try
                {
                    await runtime.StartAsync(tempDir, "test prompt", logFilePath: logFilePath);
                    await Task.Delay(2500);

                    AssertTrue(outputLines.Contains(sentinel), "OnOutputReceived should still receive the stderr sentinel when WriteStderrToLogFile is false");

                    string logContent = File.Exists(logFilePath) ? File.ReadAllText(logFilePath) : "";
                    AssertFalse(logContent.Contains(sentinel), "Log file must NOT contain the stderr sentinel when WriteStderrToLogFile is false");
                    AssertFalse(logContent.Contains("[stderr]"), "Log file must NOT contain any [stderr] line when WriteStderrToLogFile is false");
                }
                finally
                {
                    try { if (File.Exists(logFilePath)) File.Delete(logFilePath); } catch { }
                }
            });

            await RunTest("WriteStderrToLogFile False Preserves Quota Signal In Log File", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.WriteStderrToLogFileOverride = false;
                string tempDir = Path.GetTempPath();
                string quotaText = "You have hit your usage limit. try again at 11:12 AM.";
                ConfigureStderrEmitter(runtime, quotaText);

                string logFilePath = Path.Combine(Path.GetTempPath(), "armada_stderr_quota_" + Guid.NewGuid().ToString("N") + ".log");

                try
                {
                    await runtime.StartAsync(tempDir, "test prompt", logFilePath: logFilePath);
                    await Task.Delay(2500);

                    string logContent = File.Exists(logFilePath) ? File.ReadAllText(logFilePath) : "";
                    AssertTrue(logContent.Contains("[stderr] " + quotaText), "Quota-signal stderr line must be preserved in log file even when WriteStderrToLogFile is false");
                }
                finally
                {
                    try { if (File.Exists(logFilePath)) File.Delete(logFilePath); } catch { }
                }
            });

            await RunTest("WriteStderrToLogFile False Echoes Final Message On Exit", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.WriteStderrToLogFileOverride = false;
                string tempDir = Path.GetTempPath();

                string logFilePath = Path.Combine(Path.GetTempPath(), "armada_stderr_gate_" + Guid.NewGuid().ToString("N") + ".log");
                string finalMessageFilePath = Path.Combine(Path.GetTempPath(), "armada_final_msg_" + Guid.NewGuid().ToString("N") + ".txt");
                string finalMessage = "FINAL_ANSWER_" + Guid.NewGuid().ToString("N");
                File.WriteAllText(finalMessageFilePath, finalMessage);

                try
                {
                    await runtime.StartAsync(tempDir, "test prompt", logFilePath: logFilePath, finalMessageFilePath: finalMessageFilePath);
                    await Task.Delay(2500);

                    string logContent = File.Exists(logFilePath) ? File.ReadAllText(logFilePath) : "";
                    AssertTrue(logContent.Contains(finalMessage), "Log file should contain the echoed final message after exit");
                    AssertTrue(logContent.Contains("=== Final message ==="), "Log file should contain the final-message header");
                }
                finally
                {
                    try { if (File.Exists(logFilePath)) File.Delete(logFilePath); } catch { }
                    try { if (File.Exists(finalMessageFilePath)) File.Delete(finalMessageFilePath); } catch { }
                }
            });

            await RunTest("WriteStderrToLogFile Default True Writes Stderr To Log File", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                string tempDir = Path.GetTempPath();
                string sentinel = "STDERR_SENTINEL_" + Guid.NewGuid().ToString("N");
                ConfigureStderrEmitter(runtime, sentinel);

                string logFilePath = Path.Combine(Path.GetTempPath(), "armada_stderr_gate_" + Guid.NewGuid().ToString("N") + ".log");

                try
                {
                    await runtime.StartAsync(tempDir, "test prompt", logFilePath: logFilePath);
                    await Task.Delay(2500);

                    string logContent = File.Exists(logFilePath) ? File.ReadAllText(logFilePath) : "";
                    AssertTrue(logContent.Contains(sentinel), "Log file should contain the stderr sentinel when WriteStderrToLogFile defaults to true");
                }
                finally
                {
                    try { if (File.Exists(logFilePath)) File.Delete(logFilePath); } catch { }
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
