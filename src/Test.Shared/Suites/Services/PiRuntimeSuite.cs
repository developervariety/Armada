namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Runtimes;
    using SyslogLogging;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// Descriptors for the pi runtime: the <c>pi -p --mode json</c> argument builder, the effort-to-thinking mapping,
    /// the runtime-name aliases, and launches of a recording stub <c>pi</c> that prove the real command line, stdin
    /// prompt delivery and working directory.
    /// </summary>
    public sealed class PiRuntimeSuite : IArmadaTestSuite
    {
        #region Public-Methods

        /// <summary>
        /// Build the descriptor for the pi runtime suite.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            // ---- Command builder ----
            cases.Add(Case("args_default_shape", "Default pi args are exactly -p --mode json --no-session; the prompt is not an argument", TestTags.Positive, () =>
            {
                List<string> args = PiCommandBuilder.BuildRunArguments("/tmp/wd", "do the thing", null, null, false, false);
                AssertEqual("-p|--mode|json|--no-session", String.Join("|", args));
            }));

            cases.Add(Case("args_model_and_effort", "Model and thinking level are passed as --model and --thinking", TestTags.Positive, () =>
            {
                List<string> args = PiCommandBuilder.BuildRunArguments("/tmp/wd", "hi", " anthropic/claude-opus-4-8 ", "high", false, false);
                AssertEqual("-p|--mode|json|--no-session|--model|anthropic/claude-opus-4-8|--thinking|high", String.Join("|", args));
            }));

            cases.Add(Case("args_refuse_excludes_bash", "Refuse adds --exclude-tools bash; --approve is added only on request", TestTags.Positive, () =>
            {
                List<string> args = PiCommandBuilder.BuildRunArguments("/tmp/wd", "hi", null, null, true, true);
                AssertEqual("-p|--mode|json|--no-session|--approve|--exclude-tools|bash", String.Join("|", args));
            }));

            cases.Add(Case("args_reject_blank", "Blank working directory or prompt throws", TestTags.Negative, () =>
            {
                AssertThrows<ArgumentNullException>(() => PiCommandBuilder.BuildRunArguments("", "hi", null, null, false, false));
                AssertThrows<ArgumentNullException>(() => PiCommandBuilder.BuildRunArguments("  ", "hi", null, null, false, false));
                AssertThrows<ArgumentNullException>(() => PiCommandBuilder.BuildRunArguments("/tmp/wd", "", null, null, false, false));
            }));

            // ---- Reasoning effort mapping ----
            cases.Add(Case("thinking_maps_levels", "Every Armada effort maps 1:1 to a pi thinking level; null omits the flag", TestTags.Positive, () =>
            {
                AssertEqual("off", ReasoningEffortTranslator.ToPiThinkingLevel(ReasoningEffortEnum.Off));
                AssertEqual("minimal", ReasoningEffortTranslator.ToPiThinkingLevel(ReasoningEffortEnum.Minimal));
                AssertEqual("low", ReasoningEffortTranslator.ToPiThinkingLevel(ReasoningEffortEnum.Low));
                AssertEqual("medium", ReasoningEffortTranslator.ToPiThinkingLevel(ReasoningEffortEnum.Medium));
                AssertEqual("high", ReasoningEffortTranslator.ToPiThinkingLevel(ReasoningEffortEnum.High));
                AssertNull(ReasoningEffortTranslator.ToPiThinkingLevel(null));
            }));

            // ---- Runtime name aliases ----
            cases.Add(Case("parser_aliases", "pi, Pi and pi-coding-agent parse to Pi; unknown names are still rejected", TestTags.Positive, () =>
            {
                foreach (string alias in new string[] { "pi", "Pi", "PI", "pi-coding-agent", " pi " })
                {
                    AssertTrue(AgentRuntimeParser.TryParse(alias, out AgentRuntimeEnum runtime), "expected '" + alias + "' to parse");
                    AssertEqual(AgentRuntimeEnum.Pi, runtime);
                }

                AssertFalse(AgentRuntimeParser.TryParse("pie", out AgentRuntimeEnum _), "unknown runtime rejected");
                AssertFalse(AgentRuntimeParser.TryParse("pi-agent", out AgentRuntimeEnum _), "unknown runtime rejected");
                AssertContains("pi", AgentRuntimeParser.AcceptedValues);
            }));

            cases.Add(Case("enum_serializes_as_pi", "The runtime serializes as \"Pi\" and is appended after Custom", TestTags.Positive, () =>
            {
                AssertEqual("\"Pi\"", System.Text.Json.JsonSerializer.Serialize(AgentRuntimeEnum.Pi));
                AssertEqual(AgentRuntimeEnum.Pi, System.Text.Json.JsonSerializer.Deserialize<AgentRuntimeEnum>("\"Pi\""));
                AssertTrue((int)AgentRuntimeEnum.Pi > (int)AgentRuntimeEnum.Custom, "appended last so ordinals do not shift");
                AssertTrue(AgentRuntimeCapabilities.SupportsPlanningSessions(AgentRuntimeEnum.Pi), "pi supports planning sessions");
                AssertFalse(CaptainLaunchIsolationPlanner.CarriesSessionToken(AgentRuntimeEnum.Pi), "pi has no isolation plan");
                AssertEqual("pi", Armada.Core.Harbor.HarborRuntimeDetector.CommandFor(AgentRuntimeEnum.Pi));
                AssertContains("pi.dev", RuntimeDetectionService.GetInstallHint(AgentRuntimeEnum.Pi));
            }));

            // ---- Launch through a recording stub ----
            cases.Add(CaseAsync("stub_launch_records_command_line", "A launch runs pi with the expected argv, prompt on stdin and --no-session in the working directory", TestTags.Positive, async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                string root = TestTemp.NewDirectory("pi_launch");
                try
                {
                    string work = Path.Combine(root, "work");
                    Directory.CreateDirectory(work);
                    string stub = StubAgentProcesses.WritePiStub(Path.Combine(root, "stub"));
                    Captain captain = new Captain("pi", AgentRuntimeEnum.Pi) { ReasoningEffort = ReasoningEffortEnum.Medium };
                    string prompt = "line one\nline two with 'quotes' and $HOME\nline three";
                    int? code = await LaunchAsync(new PiRuntime(Logging()) { ExecutablePath = stub }, work, prompt, "openai/gpt-5.2", captain).ConfigureAwait(false);

                    AssertEqual(0, code ?? -1);
                    string[] argv = File.ReadAllLines(Path.Combine(root, "stub", "pi-argv.txt"));
                    AssertEqual("-p|--mode|json|--no-session|--model|openai/gpt-5.2|--thinking|medium", String.Join("|", argv));
                    AssertEqual(prompt, File.ReadAllText(Path.Combine(root, "stub", "pi-stdin.txt")).TrimEnd('\n'), "the multi-line prompt arrives on stdin intact");
                    AssertEqual(Path.GetFullPath(work).TrimEnd('/'), ResolveRealPath(File.ReadAllText(Path.Combine(root, "stub", "pi-cwd.txt")).Trim()), "pi runs in the working directory");
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { }
                }
            }));

            cases.Add(CaseAsync("stub_launch_refuse_excludes_bash", "A captain without auto-approve launches pi with --exclude-tools bash", TestTags.Negative, async () =>
            {
                if (OperatingSystem.IsWindows()) return;
                string root = TestTemp.NewDirectory("pi_refuse");
                try
                {
                    string work = Path.Combine(root, "work");
                    Directory.CreateDirectory(work);
                    string stub = StubAgentProcesses.WritePiStub(Path.Combine(root, "stub"));
                    Captain refuse = CaptainRuntimeOptions.WithEffectiveAutoApprove(new Captain("pi", AgentRuntimeEnum.Pi), false);
                    await LaunchAsync(new PiRuntime(Logging()) { ExecutablePath = stub }, work, "hi", null, refuse).ConfigureAwait(false);
                    string[] argv = File.ReadAllLines(Path.Combine(root, "stub", "pi-argv.txt"));
                    AssertEqual("-p|--mode|json|--no-session|--exclude-tools|bash", String.Join("|", argv));
                }
                finally
                {
                    try { Directory.Delete(root, true); } catch { }
                }
            }));

            return new TestSuiteDescriptor(
                suiteId: "Services.PiRuntime",
                displayName: "pi Runtime",
                cases: cases);
        }

        #endregion

        #region Private-Methods

        private static LoggingModule Logging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static string ResolveRealPath(string path)
        {
            // macOS temp directories are reached through /var -> /private/var; compare the resolved forms.
            if (path.StartsWith("/private/", StringComparison.Ordinal) && !Path.GetTempPath().StartsWith("/private/", StringComparison.Ordinal))
                return path.Substring("/private".Length).TrimEnd('/');
            return path.TrimEnd('/');
        }

        private static async Task<int?> LaunchAsync(PiRuntime runtime, string work, string prompt, string? model, Captain? captain, string? finalMessageFile = null)
        {
            TaskCompletionSource<int?> exited = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.OnProcessExited += (int pid, int? code) => exited.TrySetResult(code);
            await runtime.StartAsync(work, prompt, finalMessageFilePath: finalMessageFile, model: model, captain: captain).ConfigureAwait(false);
            Task finished = await Task.WhenAny(exited.Task, Task.Delay(30000)).ConfigureAwait(false);
            AssertTrue(finished == exited.Task, "stub pi did not exit");
            return await exited.Task.ConfigureAwait(false);
        }

        private static TestCaseDescriptor Case(string caseId, string displayName, string tag, Action body)
        {
            return CaseAsync(caseId, displayName, tag, () =>
            {
                body();
                return Task.CompletedTask;
            });
        }

        private static TestCaseDescriptor CaseAsync(string caseId, string displayName, string tag, Func<Task> body)
        {
            return new TestCaseDescriptor(
                suiteId: "Services.PiRuntime",
                caseId: caseId,
                displayName: displayName,
                executeAsync: (CancellationToken ct) => body(),
                tags: new List<string> { tag });
        }

        #endregion
    }
}
