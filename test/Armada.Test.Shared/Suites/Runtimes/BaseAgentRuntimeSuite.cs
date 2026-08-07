namespace Armada.Test.Shared.Suites.Runtimes
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Armada.Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for <see cref="TestAgentRuntime"/> exercising the shared <c>BaseAgentRuntime</c>
    /// behavior: constructor and argument validation, invalid process-id handling for liveness and
    /// stop, name and resume metadata, valid and invalid command launches, and output/started event
    /// propagation including UTF-8 stderr preservation. Process-launching cases spawn real short-lived
    /// subprocesses.
    /// </summary>
    public sealed class BaseAgentRuntimeSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Runtimes.BaseAgentRuntime";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the Base Agent Runtime suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("constructor_null_logging_throws", "Constructor Null Logging Throws", TestTags.Negative, () =>
            {
                AssertThrows<ArgumentNullException>(() => new TestAgentRuntime(null!));
            }));

            cases.Add(CaseAsync("start_async_null_working_directory_throws", "StartAsync Null WorkingDirectory Throws", TestTags.Negative, async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                await AssertThrowsAsync<ArgumentNullException>(() => runtime.StartAsync(null!, "prompt"));
            }));

            cases.Add(CaseAsync("start_async_empty_working_directory_throws", "StartAsync Empty WorkingDirectory Throws", TestTags.Negative, async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                await AssertThrowsAsync<ArgumentNullException>(() => runtime.StartAsync("", "prompt"));
            }));

            cases.Add(CaseAsync("start_async_null_prompt_throws", "StartAsync Null Prompt Throws", TestTags.Negative, async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                await AssertThrowsAsync<ArgumentNullException>(() => runtime.StartAsync("/tmp", null!));
            }));

            cases.Add(CaseAsync("start_async_empty_prompt_throws", "StartAsync Empty Prompt Throws", TestTags.Negative, async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                await AssertThrowsAsync<ArgumentNullException>(() => runtime.StartAsync("/tmp", ""));
            }));

            cases.Add(CaseAsync("is_running_async_invalid_process_id_returns_false", "IsRunningAsync Invalid ProcessId Returns False", TestTags.Negative, async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                bool running = await runtime.IsRunningAsync(99999999);
                AssertFalse(running);
            }));

            cases.Add(CaseAsync("stop_async_invalid_process_id_does_not_throw", "StopAsync Invalid ProcessId Does Not Throw", TestTags.Negative, async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                await runtime.StopAsync(99999999);
            }));

            cases.Add(Case("name_returns_expected", "Name Returns Expected", TestTags.Positive, () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                AssertEqual("TestRuntime", runtime.Name);
            }));

            cases.Add(Case("supports_resume_returns_false", "SupportsResume Returns False", TestTags.Positive, () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                AssertFalse(runtime.SupportsResume);
            }));

            cases.Add(CaseAsync("start_async_valid_command_returns_process_id", "StartAsync Valid Command Returns ProcessId", TestTags.Positive, async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                string tempDir = Path.GetTempPath();

                int pid = await runtime.StartAsync(tempDir, "test prompt");
                AssertTrue(pid > 0);

                // Wait briefly for process to finish (dotnet --version exits quickly)
                await Task.Delay(2000);
            }));

            cases.Add(CaseAsync("start_async_invalid_command_throws", "StartAsync Invalid Command Throws", TestTags.Negative, async () =>
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
            }));

            cases.Add(CaseAsync("on_output_received_fires_for_output", "OnOutputReceived Fires For Output", TestTags.Positive, async () =>
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
            }));

            cases.Add(CaseAsync("on_output_received_preserves_utf8_stderr_content", "OnOutputReceived Preserves Utf8 Stderr Content", TestTags.Positive, async () =>
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
            }));

            cases.Add(CaseAsync("on_process_started_fires_with_pid", "OnProcessStarted Fires WithPid", TestTags.Positive, async () =>
            {
                TestAgentRuntime runtime = new TestAgentRuntime(CreateLogging());
                string tempDir = Path.GetTempPath();

                int startedPid = 0;
                runtime.OnProcessStarted += pid => startedPid = pid;

                int pid = await runtime.StartAsync(tempDir, "test prompt");

                AssertTrue(pid > 0, "Expected a valid PID");
                AssertEqual(pid, startedPid, "OnProcessStarted should fire with the launched process PID");

                await Task.Delay(1000);
            }));

            return new TestSuiteDescriptor(
                suiteId: SuiteId,
                displayName: "Base Agent Runtime",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) =>
                {
                    body();
                    return Task.CompletedTask;
                },
                tags: new List<string> { tag });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: SuiteId,
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
