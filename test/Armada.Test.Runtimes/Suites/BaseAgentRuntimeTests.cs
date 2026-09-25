namespace Armada.Test.Runtimes.Suites
{
    using System.Diagnostics;
    using Armada.Core.Services;
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

        private static bool WaitForProcessExit(int pid, TimeSpan within)
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
                catch (InvalidOperationException)
                {
                    return true;
                }

                Thread.Sleep(100);
            }

            return false;
        }

        private static void KillQuietly(int pid)
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

            await RunTest("StartAsync_AppliesCaptainLaunchIsolationPlan", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.CaptureStartInfoAndThrow = true;
                CaptainLaunchIsolationPlan plan = new CaptainLaunchIsolationPlan();
                plan.ExtraArguments.Add("--mcp-config");
                plan.ExtraArguments.Add(Path.Combine(Path.GetTempPath(), "armada-mcp.json"));
                plan.EnvironmentOverrides["ARMADA_TEST_SCOPED_CONFIG"] = "enabled";

                await AssertThrowsAsync<InvalidOperationException>(() =>
                    runtime.StartAsync(Path.GetTempPath(), "test prompt", isolationPlan: plan));

                AssertTrue(runtime.CapturedStartInfo != null, "Expected StartAsync to expose ProcessStartInfo before launch");
                ProcessStartInfo startInfo = runtime.CapturedStartInfo!;
                AssertTrue(startInfo.ArgumentList.Contains("--mcp-config"), "Expected launch-scoped MCP argument");
                AssertEqual("enabled", startInfo.Environment["ARMADA_TEST_SCOPED_CONFIG"]);
            });

            if (OperatingSystem.IsWindows())
            {
                SkipTest("Stop And Liveness Refuse A Process Whose Start Time Differs From The Recorded Launch", "the stand-in process is a POSIX sleep");
            }
            else await RunTest("Stop And Liveness Refuse A Process Whose Start Time Differs From The Recorded Launch", async () =>
            {
                // A recorded launch with this identifier started an hour earlier, so the live process holding the
                // identifier now is a different one: the operating system reused the number.
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                using (Process stranger = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!)
                {
                    try
                    {
                        DateTime actualStartUtc = stranger.StartTime.ToUniversalTime();
                        Armada.Core.ProcessSupervisor.RecordLaunchedProcess(stranger.Id, actualStartUtc.AddHours(-1));

                        AssertFalse(await runtime.IsRunningAsync(stranger.Id), "a reused identifier does not read as the launched agent");
                        AssertFalse(Armada.Core.ProcessSupervisor.IsTrackedProcessAlive(stranger.Id), "the health checks' liveness lookup does not read a reused identifier as alive");
                        Armada.Core.Models.AgentStopResult refused = await runtime.StopAsync(stranger.Id);
                        AssertFalse(stranger.WaitForExit(500), "stop must not kill a process that is not the recorded launch");
                        AssertEqual(Armada.Core.Enums.AgentStopOutcomeEnum.Refused, refused.Outcome, "a stop that left the process running reports a refusal");
                        AssertEqual(Armada.Core.Models.AgentStopResult.IdentifierReusedReason, refused.Reason, "the refusal names the reused identifier");

                        Armada.Core.ProcessSupervisor.RecordLaunchedProcess(stranger.Id, actualStartUtc);
                        AssertTrue(await runtime.IsRunningAsync(stranger.Id), "the recorded launch reads as running");
                        AssertTrue(Armada.Core.ProcessSupervisor.IsTrackedProcessAlive(stranger.Id), "the health checks read the recorded launch as alive");
                        Armada.Core.Models.AgentStopResult stopped = await runtime.StopAsync(stranger.Id);
                        AssertTrue(stranger.WaitForExit(5000), "stop kills the process whose start time matches the recorded launch");
                        AssertEqual(Armada.Core.Enums.AgentStopOutcomeEnum.Stopped, stopped.Outcome, "a verified stop reports stopped");
                    }
                    finally
                    {
                        try { stranger.Kill(); } catch (InvalidOperationException) { }
                    }
                }
            });

            if (OperatingSystem.IsWindows())
            {
                SkipTest("Stop Leaves Alone A Live Process With No Recorded Launch", "the stand-in process is a POSIX sleep");
            }
            else await RunTest("Stop Leaves Alone A Live Process With No Recorded Launch", async () =>
            {
                // Nothing proves this process is one the runtime launched (the record is lost when the admiral
                // process restarts), so a stop must not kill it; it still reads as running.
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                using (Process stranger = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!)
                {
                    try
                    {
                        AssertEqual(Armada.Core.Enums.LaunchedProcessIdentityEnum.Unverified, Armada.Core.ProcessSupervisor.ProbeLaunchedProcess(stranger.Id), "a live process with no recorded launch is unverified");
                        AssertTrue(await runtime.IsRunningAsync(stranger.Id), "an unverified process reads as running");
                        Armada.Core.Models.AgentStopResult refused = await runtime.StopAsync(stranger.Id);
                        AssertFalse(stranger.WaitForExit(500), "stop must not kill a process whose launch identity is unknown");
                        AssertEqual(Armada.Core.Enums.AgentStopOutcomeEnum.Refused, refused.Outcome, "the stop is reported as refused, not stopped");
                        AssertEqual(Armada.Core.Models.AgentStopResult.IdentityUnverifiedReason, refused.Reason, "the refusal names the unverified identity");
                    }
                    finally
                    {
                        try { stranger.Kill(); } catch (InvalidOperationException) { }
                    }
                }
            });

            if (OperatingSystem.IsWindows())
            {
                SkipTest("Stop After An Admiral Restart Kills The Agent Verified By Its Stored Start Time", "the stand-in agent is a POSIX sleep");
            }
            else await RunTest("Stop After An Admiral Restart Kills The Agent Verified By Its Stored Start Time", async () =>
            {
                string databasePath = Path.Combine(Path.GetTempPath(), "armada_launch_identity_" + Guid.NewGuid().ToString("N") + ".db");
                string connectionString = "Data Source=" + databasePath + ";Pooling=False";
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.CommandOverride = "sleep";
                runtime.ArgsOverride = new List<string> { "30" };
                int pid = 0;
                try
                {
                    pid = await runtime.StartAsync(Path.GetTempPath(), "test prompt");
                    DateTime? recordedStartUtc = Armada.Core.ProcessSupervisor.GetRecordedLaunchStartUtc(pid);
                    AssertTrue(recordedStartUtc.HasValue, "the launch records the agent's start time");

                    // The admiral stores the launch identity next to the process identifier on the captain record.
                    using (Armada.Core.Database.Sqlite.SqliteDatabaseDriver database = new Armada.Core.Database.Sqlite.SqliteDatabaseDriver(connectionString, CreateLogging()))
                    {
                        await database.InitializeAsync();
                        Armada.Core.Models.Captain captain = new Armada.Core.Models.Captain("restart-captain", Armada.Core.Enums.AgentRuntimeEnum.ClaudeCode);
                        captain.ProcessId = pid;
                        captain.ProcessStartedUtc = recordedStartUtc;
                        await database.Captains.CreateAsync(captain);
                    }

                    // A restart leaves no in-memory launch record: the agent is unverified and a stop refuses it.
                    Armada.Core.ProcessSupervisor.ForgetLaunchedProcess(pid);
                    Armada.Core.Models.AgentStopResult beforeRestore = await runtime.StopAsync(pid);
                    AssertEqual(Armada.Core.Enums.AgentStopOutcomeEnum.Refused, beforeRestore.Outcome, "without its stored identity the agent is not stopped");
                    AssertFalse(WaitForProcessExit(pid, TimeSpan.FromMilliseconds(300)), "the refused stop leaves the agent running");

                    // The new admiral process restores the stored identity from a freshly opened database.
                    using (Armada.Core.Database.Sqlite.SqliteDatabaseDriver reopened = new Armada.Core.Database.Sqlite.SqliteDatabaseDriver(connectionString, CreateLogging()))
                    {
                        await reopened.InitializeAsync();
                        Armada.Core.Services.ProcessLaunchIdentityRestoreResult restored = await Armada.Core.Services.ProcessLaunchIdentityRestore.RestoreAsync(reopened);
                        AssertEqual(1, restored.Restored, "the stored captain identity is restored");
                    }

                    AssertEqual(Armada.Core.Enums.LaunchedProcessIdentityEnum.Verified, Armada.Core.ProcessSupervisor.ProbeLaunchedProcess(pid), "the pre-restart agent is verified by its stored start time");
                    Armada.Core.Models.AgentStopResult afterRestore = await runtime.StopAsync(pid);
                    AssertEqual(Armada.Core.Enums.AgentStopOutcomeEnum.Stopped, afterRestore.Outcome, "the verified agent is stopped");
                    AssertTrue(WaitForProcessExit(pid, TimeSpan.FromSeconds(5)), "the pre-restart agent is gone after the stop");
                }
                finally
                {
                    if (pid > 0) KillQuietly(pid);
                    try { File.Delete(databasePath); } catch (IOException) { }
                }
            });

            if (OperatingSystem.IsWindows())
            {
                SkipTest("StartAsync With A Cancelled Token Launches Nothing", "the stand-in agent is a POSIX sleep");
            }
            else await RunTest("StartAsync With A Cancelled Token Launches Nothing", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.CommandOverride = "sleep";
                runtime.ArgsOverride = new List<string> { "30" };
                int startedPid = 0;
                runtime.OnProcessStarted += pid => startedPid = pid;

                using (CancellationTokenSource cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    bool threw = false;
                    int returnedPid = 0;
                    try
                    {
                        returnedPid = await runtime.StartAsync(Path.GetTempPath(), "test prompt", token: cancelled.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        threw = true;
                    }

                    if (returnedPid > 0) KillQuietly(returnedPid);
                    AssertTrue(threw, "a cancelled launch reports the cancellation");
                    AssertEqual(0, startedPid, "no process is started for a cancelled launch");
                }
            });

            if (OperatingSystem.IsWindows())
            {
                SkipTest("StartAsync Cancelled After The Process Started Kills The Child", "the stand-in agent is a POSIX sleep");
            }
            else await RunTest("StartAsync Cancelled After The Process Started Kills The Child", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.CommandOverride = "sleep";
                runtime.ArgsOverride = new List<string> { "30" };

                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    int startedPid = 0;
                    runtime.OnProcessStarted += pid =>
                    {
                        startedPid = pid;
                        cancel.Cancel();
                    };

                    bool threw = false;
                    try
                    {
                        await runtime.StartAsync(Path.GetTempPath(), "test prompt", token: cancel.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        threw = true;
                    }

                    AssertTrue(startedPid > 0, "the process started before the cancellation");
                    bool exited = WaitForProcessExit(startedPid, TimeSpan.FromSeconds(10));
                    if (!exited) KillQuietly(startedPid);
                    AssertTrue(threw, "a launch cancelled after start reports the cancellation");
                    AssertTrue(exited, "a launch that fails after start kills the child it started");
                }
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

            await RunTest("UsePromptStdin_LogsPromptContentWithRolePreamble", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.UsePromptStdinOverride = true;
                string tempDir = Path.GetTempPath();
                string rolePreamble = "Role: You are an Armada worker agent.";
                string prompt = rolePreamble + " Mission: test objective. Branch: main. Read CLAUDE.md.";

                string logFilePath = Path.Combine(Path.GetTempPath(), "armada_stdin_prompt_" + Guid.NewGuid().ToString("N") + ".log");

                try
                {
                    await runtime.StartAsync(tempDir, prompt, logFilePath: logFilePath);
                    await Task.Delay(2500);

                    string logContent = File.Exists(logFilePath) ? File.ReadAllText(logFilePath) : "";
                    AssertTrue(logContent.Contains(rolePreamble), "Log file must contain the role preamble when the prompt is delivered via stdin");
                    AssertTrue(logContent.Contains("Mission: test objective"), "Log file must contain the mission instructions when the prompt is delivered via stdin");
                    AssertTrue(logContent.Contains("Read CLAUDE.md"), "Log file must contain the read-instruction when the prompt is delivered via stdin");
                }
                finally
                {
                    try { if (File.Exists(logFilePath)) File.Delete(logFilePath); } catch { }
                }
            });

            await RunTest("RedirectedStdin_EncodingWritesNoByteOrderMark", async () =>
            {
                // Process.Start writes the stdin encoding's preamble and flushes it before returning. A byte-order
                // mark would reach the agent ahead of its prompt, and against an agent that has already exited
                // that write makes Start itself throw a broken pipe.
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.CaptureStartInfoAndThrow = true;
                try { await runtime.StartAsync(Path.GetTempPath(), "test prompt"); } catch (InvalidOperationException) { }
                AssertNotNull(runtime.CapturedStartInfo, "the start info is captured");
                AssertNotNull(runtime.CapturedStartInfo!.StandardInputEncoding, "redirected stdin names its encoding");
                AssertEqual(0, runtime.CapturedStartInfo.StandardInputEncoding!.GetPreamble().Length, "the stdin encoding writes no preamble");
            });

            if (OperatingSystem.IsWindows())
            {
                SkipTest("UsePromptStdin_AgentReceivesExactlyThePromptBytes", "the stdin capture child is a POSIX shell script");
            }
            else await RunTest("UsePromptStdin_AgentReceivesExactlyThePromptBytes", async () =>
            {
                string scratch = Path.Combine(Path.GetTempPath(), "armada_stdin_bytes_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                try
                {
                    string output = Path.Combine(scratch, "stdin.bin");
                    TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                    runtime.UsePromptStdinOverride = true;
                    runtime.CommandOverride = "/bin/sh";
                    runtime.ArgsOverride = new List<string> { "-c", "cat > \"$STDIN_CAPTURE.tmp\" && mv \"$STDIN_CAPTURE.tmp\" \"$STDIN_CAPTURE\"" };
                    string prompt = "Role: worker. Mission: caf\u00e9 \u2014 exact bytes.";
                    await runtime.StartAsync(scratch, prompt, environment: new Dictionary<string, string> { ["STDIN_CAPTURE"] = output });
                    DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!File.Exists(output) && DateTime.UtcNow < deadline) await Task.Delay(50);
                    AssertTrue(File.Exists(output), "the child wrote what it read from stdin");
                    byte[] received = File.ReadAllBytes(output);
                    AssertEqual(Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(prompt)), Convert.ToHexString(received), "the agent reads the prompt's UTF-8 bytes and nothing before them");
                }
                finally
                {
                    try { Directory.Delete(scratch, true); } catch { }
                }
            });

            await RunTest("WriteStderrToLogFile False Preserves Standalone Reset Time Line", async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                runtime.WriteStderrToLogFileOverride = false;
                string tempDir = Path.GetTempPath();
                string resetLine = "try again at 11:12 AM";
                ConfigureStderrEmitter(runtime, resetLine);

                string logFilePath = Path.Combine(Path.GetTempPath(), "armada_stderr_reset_" + Guid.NewGuid().ToString("N") + ".log");

                try
                {
                    await runtime.StartAsync(tempDir, "test prompt", logFilePath: logFilePath);
                    await Task.Delay(2500);

                    string logContent = File.Exists(logFilePath) ? File.ReadAllText(logFilePath) : "";
                    AssertTrue(logContent.Contains("[stderr] " + resetLine), "Standalone reset-time stderr line must be preserved in log file even when WriteStderrToLogFile is false");
                }
                finally
                {
                    try { if (File.Exists(logFilePath)) File.Delete(logFilePath); } catch { }
                }
            });
        }
    }
}
