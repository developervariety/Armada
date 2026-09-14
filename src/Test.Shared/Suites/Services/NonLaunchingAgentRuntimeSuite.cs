namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Armada.Runtimes;
    using Armada.Runtimes.Interfaces;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Behaviour of the test-host runtime seam: the non-launching runtime, the runtime factory opt-in, the
    /// launch log check and the provider environment removal.
    /// </summary>
    public sealed class NonLaunchingAgentRuntimeSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string _SuiteId = "Services.NonLaunchingAgentRuntime";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("start_registers_synthetic_process_and_stop_from_any_instance_reports_exit", TestTags.Positive, async () =>
            {
                string dir = TestTemp.NewDirectory("nonlaunching");
                NonLaunchingAgentRuntime starter = new NonLaunchingAgentRuntime(AgentRuntimeEnum.Codex);
                int startedPid = 0;
                List<int?> exits = new List<int?>();
                starter.OnProcessStarted += pid => startedPid = pid;
                starter.OnProcessExited += (pid, code) => exits.Add(code);

                int processId = await starter.StartAsync(dir, "prompt", logFilePath: Path.Combine(dir, "mission.log"));

                AssertTrue(processId > NonLaunchingAgentRuntime.FirstProcessId && processId < 2_000_000_000, "synthetic identifier range: " + processId);
                AssertEqual(processId, startedPid, "OnProcessStarted identifier");
                AssertTrue(ProcessSupervisor.IsTrackedProcessAlive(processId), "health checks see the start as alive");
                AssertTrue(await starter.IsRunningAsync(processId), "runtime reports running");
                TestProcessLaunch launch = TestProcessLaunchLog.All().Last(l => l.ProcessId == processId);
                AssertFalse(launch.LaunchedProcess, "no process launched");
                AssertEqual(AgentRuntimeEnum.Codex, launch.RuntimeType, "recorded runtime");

                await new NonLaunchingAgentRuntime(AgentRuntimeEnum.Codex).StopAsync(processId);

                AssertEqual(1, exits.Count, "one exit raised on the starting instance");
                AssertEqual<int?>(NonLaunchingAgentRuntime.StoppedExitCode, exits[0], "stop exit code");
                AssertFalse(ProcessSupervisor.IsTrackedProcessAlive(processId), "health checks see the stop");
                AssertContains("Agent exited with code " + NonLaunchingAgentRuntime.StoppedExitCode, File.ReadAllText(Path.Combine(dir, "mission.log")));
            }));

            cases.Add(Case("explicit_exit_reports_the_given_code_once", TestTags.Negative, async () =>
            {
                string dir = TestTemp.NewDirectory("nonlaunching");
                NonLaunchingAgentRuntime runtime = new NonLaunchingAgentRuntime(AgentRuntimeEnum.ClaudeCode);
                List<int?> exits = new List<int?>();
                runtime.OnProcessExited += (pid, code) => exits.Add(code);
                int processId = await runtime.StartAsync(dir, "prompt");

                AssertTrue(NonLaunchingAgentRuntime.Exit(processId, 1), "first exit is reported");
                AssertFalse(NonLaunchingAgentRuntime.Exit(processId, 0), "second exit is refused");
                AssertEqual(1, exits.Count, "one exit raised");
                AssertEqual<int?>(1, exits[0], "explicit exit code");
                AssertFalse(await runtime.IsRunningAsync(processId), "not running after exit");
            }));

            cases.Add(Case("factory_creates_test_runtime_unless_opted_in", TestTags.Positive, () =>
            {
                LoggingModule logging = new LoggingModule();
                logging.Settings.EnableConsole = false;
                ArmadaSettings settings = new ArmadaSettings();

                TestAgentRuntimeFactory byDefault = new TestAgentRuntimeFactory(logging, settings);
                foreach (AgentRuntimeEnum runtime in new[] { AgentRuntimeEnum.ClaudeCode, AgentRuntimeEnum.Codex, AgentRuntimeEnum.Gemini, AgentRuntimeEnum.Cursor, AgentRuntimeEnum.OpenCode, AgentRuntimeEnum.Mux })
                {
                    AssertTrue(byDefault.Create(runtime) is NonLaunchingAgentRuntime, runtime + " is the test runtime by default");
                }

                TestAgentRuntimeFactory optedIn = new TestAgentRuntimeFactory(logging, settings, new[] { AgentRuntimeEnum.ClaudeCode });
                AssertTrue(optedIn.Create(AgentRuntimeEnum.ClaudeCode) is ClaudeCodeRuntime, "opted-in runtime is the real adapter");
                AssertTrue(optedIn.Create(AgentRuntimeEnum.Codex) is NonLaunchingAgentRuntime, "other runtimes stay the test runtime");
                return Task.CompletedTask;
            }));

            cases.Add(Case("opt_in_names_are_parsed_and_non_cli_runtimes_rejected", TestTags.Negative, () =>
            {
                IReadOnlyCollection<AgentRuntimeEnum> parsed = TestAgentRuntimeFactory.ParseRuntimeNames(" claudecode, Codex ");
                AssertEqual(2, parsed.Count, "two runtimes parsed");
                AssertTrue(parsed.Contains(AgentRuntimeEnum.ClaudeCode) && parsed.Contains(AgentRuntimeEnum.Codex), "parsed names");
                AssertEqual(0, TestAgentRuntimeFactory.ParseRuntimeNames(null).Count, "unset means none");

                foreach (string invalid in new[] { "ApiEndpoint", "Custom", "NotARuntime" })
                {
                    bool threw = false;
                    try { TestAgentRuntimeFactory.ParseRuntimeNames(invalid); }
                    catch (ArgumentException ex)
                    {
                        threw = true;
                        AssertContains(invalid, ex.Message);
                    }
                    AssertTrue(threw, invalid + " is rejected");
                }
                return Task.CompletedTask;
            }));

            cases.Add(Case("real_runtime_skip_reason_names_opt_in_and_provisioning", TestTags.Negative, () =>
            {
                string? notOptedIn = TestAgentRuntimeFactory.RealRuntimeSkipReason(AgentRuntimeEnum.ClaudeCode, new List<AgentRuntimeEnum>());
                AssertNotNull(notOptedIn);
                AssertContains("not opted in", notOptedIn!);
                AssertContains(TestAgentRuntimeFactory.RealRuntimesVariable + "=ClaudeCode", notOptedIn!);

                string emptyPath = TestTemp.NewDirectory("empty-path");
                string command = TestAgentRuntimeFactory.ResolveCommand(AgentRuntimeEnum.ClaudeCode);
                string? notProvisioned = TestAgentRuntimeFactory.RealRuntimeSkipReason(
                    AgentRuntimeEnum.ClaudeCode, new[] { AgentRuntimeEnum.ClaudeCode }, emptyPath);
                AssertNotNull(notProvisioned);
                AssertContains("not provisioned", notProvisioned!);
                AssertContains("'" + command + "'", notProvisioned!);

                File.WriteAllText(Path.Combine(emptyPath, command), "");
                AssertNull(TestAgentRuntimeFactory.RealRuntimeSkipReason(
                    AgentRuntimeEnum.ClaudeCode, new[] { AgentRuntimeEnum.ClaudeCode }, emptyPath), "a provisioned, opted-in runtime runs");
                return Task.CompletedTask;
            }));

            cases.Add(Case("launch_check_names_each_unpermitted_process", TestTags.Negative, () =>
            {
                List<TestProcessLaunch> launches = new List<TestProcessLaunch>
                {
                    new TestProcessLaunch { RuntimeType = AgentRuntimeEnum.ClaudeCode, LaunchedProcess = true, ProcessId = 11, ProcessName = "claude", ExitCode = 1 },
                    new TestProcessLaunch { RuntimeType = AgentRuntimeEnum.ClaudeCode, LaunchedProcess = true, ProcessId = 12, ProcessName = "claude" },
                    new TestProcessLaunch { RuntimeType = AgentRuntimeEnum.Codex, LaunchedProcess = false, ProcessId = 13 }
                };

                string? failure = TestProcessLaunchLog.DescribeUnpermittedProcessLaunches(launches, new List<AgentRuntimeEnum>());
                AssertNotNull(failure);
                AssertContains("started 2 agent process(es)", failure!);
                AssertContains("claude x2", failure!);
                AssertContains("pid 11 exit 1", failure!);
                AssertNull(TestProcessLaunchLog.DescribeUnpermittedProcessLaunches(launches, new[] { AgentRuntimeEnum.ClaudeCode }), "opted-in launches pass");
                return Task.CompletedTask;
            }));

            cases.Add(Case("provider_variables_are_classified_and_removed", TestTags.Positive, () =>
            {
                foreach (string name in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "OPENAI_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDECODE", "GOOGLE_API_KEY" })
                    AssertTrue(TestProcessEnvironment.IsProviderVariable(name), name + " is a provider variable");
                foreach (string name in new[] { "PATH", "HOME", "ARMADA_TEST_SUITES", TestAgentRuntimeFactory.RealRuntimesVariable, "" })
                    AssertFalse(TestProcessEnvironment.IsProviderVariable(name), name + " is kept");

                if (TestProcessEnvironment.KeepRequested()) return Task.CompletedTask;

                string probe = "ANTHROPIC_ARMADA_TEST_PROBE_" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
                Environment.SetEnvironmentVariable(probe, "value");
                List<string> removed = TestProcessEnvironment.RemoveProviderVariables();
                AssertTrue(removed.Contains(probe), "probe variable reported as removed");
                AssertNull(Environment.GetEnvironmentVariable(probe), "probe variable removed");
                return Task.CompletedTask;
            }));

            return new TestSuiteDescriptor(_SuiteId, "Non-Launching Agent Runtime", cases);
        }

        #endregion

        #region Private-Methods

        private static TestCaseDescriptor Case(string caseId, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: _SuiteId,
                caseId: caseId,
                displayName: caseId,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
