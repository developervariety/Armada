namespace Test.Shared.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Test.Shared.Infrastructure;
    using Touchstone.Core;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>
    /// A Harbor started from Finder or a login item has launchd's minimal PATH. At startup it imports the login shell's
    /// PATH (and the usual tool folders), and it advertises an agent runtime as available only when that runtime's CLI
    /// resolves on PATH.
    /// </summary>
    public sealed class HarborRuntimeDetectionSuite : IArmadaTestSuite
    {
        #region Private-Members

        private const string SuiteId = "Services.HarborRuntimeDetection";
        private const string LaunchdPath = "/usr/bin:/bin:/usr/sbin:/sbin";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the suite descriptor.
        /// </summary>
        /// <returns>The suite descriptor.</returns>
        public TestSuiteDescriptor Build()
        {
            List<TestCaseDescriptor> cases = new List<TestCaseDescriptor>();

            cases.Add(Case("login_shell_path_is_imported_first", "The login shell's PATH comes first, the launchd PATH is kept, and existing tool folders are added", TestTags.Positive, () =>
            {
                if (OperatingSystem.IsWindows()) return;
                List<string> calls = new List<string>();
                HarborShellPath importer = new HarborShellPath
                {
                    RunShell = (shell, arguments, timeout) =>
                    {
                        calls.Add(shell + " " + String.Join(" ", arguments));
                        return "Welcome banner from .zprofile\n" + HarborShellPath.StartMarker + "/Users/u/.local/bin:/opt/homebrew/bin:/usr/bin:/bin" + HarborShellPath.EndMarker;
                    },
                    DirectoryExists = directory => directory == "/Users/u/.dotnet/tools" || directory == "/usr/local/bin"
                };

                HarborPathImportResult result = importer.Compute(LaunchdPath, "/bin/zsh", "/Users/u", false, true);

                AssertTrue(result.LoginShellRead, "login shell read");
                AssertEqual(1, calls.Count);
                AssertStartsWith("/bin/zsh -l -c ", calls[0], "runs the user's shell as a login shell");
                AssertEqual("/Users/u/.local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin:/Users/u/.dotnet/tools:/usr/local/bin", result.Path);
                AssertEqual("/Users/u/.local/bin,/opt/homebrew/bin,/Users/u/.dotnet/tools,/usr/local/bin", String.Join(",", result.Added));
            }));

            cases.Add(Case("shell_timeout_still_adds_tool_folders", "When the login shell does not answer, the existing tool folders are still added and the error is reported", TestTags.Negative, () =>
            {
                if (OperatingSystem.IsWindows()) return;
                HarborShellPath importer = new HarborShellPath
                {
                    RunShell = (shell, arguments, timeout) => null,
                    DirectoryExists = directory => directory == "/home/u/.local/bin"
                };

                HarborPathImportResult result = importer.Compute(LaunchdPath, null, "/home/u", false, false);
                AssertFalse(result.LoginShellRead);
                AssertEqual("/bin/bash", result.Shell, "the platform default shell when SHELL is unset");
                AssertNotNull(result.Error, "the failure is reported");
                AssertEqual(LaunchdPath + ":/home/u/.local/bin", result.Path);
            }));

            cases.Add(Case("profile_noise_without_markers_is_rejected", "Output without the PATH markers is not taken as a PATH", TestTags.Negative, () =>
            {
                HarborShellPath importer = new HarborShellPath { RunShell = (shell, arguments, timeout) => "zsh: command not found: compinit", DirectoryExists = directory => false };
                string? path = importer.ReadLoginShellPath("/bin/zsh", out string? error);
                AssertNull(path);
                AssertNotNull(error);
            }));

            cases.Add(Case("real_login_shell_path_is_read", "The default runner reads a real login shell's PATH", TestTags.Process, () =>
            {
                if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh")) return;
                string? path = new HarborShellPath().ReadLoginShellPath("/bin/sh", out string? error);
                AssertNotNull(path, "PATH read: " + (error ?? ""));
                AssertContains("/bin", path!, "a real PATH");
            }));

            cases.Add(Case("windows_path_is_unchanged", "On Windows the PATH is left alone and no shell is run", TestTags.Positive, () =>
            {
                bool ran = false;
                HarborShellPath importer = new HarborShellPath { RunShell = (shell, arguments, timeout) => { ran = true; return null; } };
                HarborPathImportResult result = importer.Compute("C:\\Windows", null, "C:\\Users\\u", true, false);
                AssertFalse(ran);
                AssertEqual("C:\\Windows", result.Path);
                AssertEqual(0, result.Added.Count);
            }));

            cases.Add(Case("runtime_available_only_when_cli_resolves", "Each runtime is available only when its CLI resolves; runtimes without a CLI are always available", TestTags.Positive, () =>
            {
                List<string> asked = new List<string>();
                HarborRuntimeDetector detector = new HarborRuntimeDetector
                {
                    Resolver = command =>
                    {
                        asked.Add(command);
                        return command == "claude" ? "/Users/u/.local/bin/claude" : command == "mux" ? "/Users/u/.dotnet/tools/mux" : null;
                    }
                };

                List<HarborCapability> capabilities = detector.Detect();
                Dictionary<string, HarborCapability> byName = new Dictionary<string, HarborCapability>(StringComparer.Ordinal);
                foreach (HarborCapability capability in capabilities) byName[capability.Name] = capability;

                AssertTrue(byName["ClaudeCode"].Available, "claude resolves");
                AssertEqual("/Users/u/.local/bin/claude", byName["ClaudeCode"].Detail);
                AssertTrue(byName["Mux"].Available, "mux resolves");
                AssertFalse(byName["Codex"].Available, "codex does not");
                AssertContains("codex", byName["Codex"].Detail ?? String.Empty, "the detail names the missing command");
                AssertFalse(byName["Gemini"].Available);
                AssertFalse(byName["Cursor"].Available);
                AssertFalse(byName["OpenCode"].Available);
                AssertTrue(byName["ApiEndpoint"].Available, "no CLI needed");
                AssertTrue(byName["Custom"].Available, "no CLI known");
                AssertFalse(byName["Pi"].Available);
                AssertEqual("claude,codex,gemini,cursor-agent,mux,opencode,pi", String.Join(",", asked), "the command each runtime class launches");
                AssertEqual(Enum.GetValues<AgentRuntimeEnum>().Length, capabilities.Count, "one capability per runtime");
            }));

            cases.Add(Case("pi_needs_help_identity", "A 'pi' on PATH is available only when the verifier confirms its --help identity", TestTags.Negative, () =>
            {
                List<string> verified = new List<string>();
                bool isPi = false;
                HarborRuntimeDetector detector = new HarborRuntimeDetector
                {
                    Resolver = command => command == "pi" || command == "claude" ? "/usr/local/bin/" + command : null,
                    Verifier = (runtime, path) =>
                    {
                        verified.Add(runtime + "=" + path);
                        return runtime != AgentRuntimeEnum.Pi || isPi;
                    }
                };

                Dictionary<string, HarborCapability> notPi = new Dictionary<string, HarborCapability>(StringComparer.Ordinal);
                foreach (HarborCapability capability in detector.Detect()) notPi[capability.Name] = capability;
                AssertFalse(notPi["Pi"].Available, "a 'pi' that is not the pi coding agent is not advertised");
                AssertContains("is not the Pi CLI", notPi["Pi"].Detail ?? String.Empty);
                AssertTrue(notPi["ClaudeCode"].Available, "other runtimes are unaffected");
                AssertTrue(verified.Contains("Pi=/usr/local/bin/pi"), "the verifier receives the resolved path");

                isPi = true;
                Dictionary<string, HarborCapability> real = new Dictionary<string, HarborCapability>(StringComparer.Ordinal);
                foreach (HarborCapability capability in detector.Detect()) real[capability.Name] = capability;
                AssertTrue(real["Pi"].Available, "a verified pi is advertised");
                AssertEqual("/usr/local/bin/pi", real["Pi"].Detail);
            }));

            cases.Add(Case("pi_help_identity_check", "pi is identified by the first line of --help, not --version", TestTags.Positive, () =>
            {
                AssertTrue(PiCommandBuilder.IsPiHelpOutput("pi - AI coding assistant with read, bash, edit, write tools\n\nUsage:\n"));
                AssertTrue(PiCommandBuilder.IsPiHelpOutput("\r\npi - AI coding assistant\r\n"), "leading blank lines and CRLF are tolerated");
                AssertFalse(PiCommandBuilder.IsPiHelpOutput("1.4.0"), "a bare version does not identify pi");
                AssertFalse(PiCommandBuilder.IsPiHelpOutput("usage: pi [options]\npi - AI coding assistant"), "only the first line counts");
                AssertFalse(PiCommandBuilder.IsPiHelpOutput(null));
                AssertFalse(PiCommandBuilder.IsPiHelpOutput("   "));
                if (OperatingSystem.IsWindows()) return;
                string directory = TestTemp.NewDirectory("fake_pi");
                try
                {
                    string fake = Path.Combine(directory, "pi");
                    File.WriteAllText(fake, "#!/bin/sh\necho 'Raspberry Pi tool 2.0'\n");
                    File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    AssertFalse(RuntimeDetectionService.IsPiCommand(fake), "a different 'pi' is rejected");
                    File.WriteAllText(fake, "#!/bin/sh\n[ \"$1\" = --help ] && echo 'pi - AI coding assistant with read, bash, edit, write tools'\n");
                    AssertTrue(RuntimeDetectionService.IsPiCommand(fake), "a command whose --help identifies pi is accepted");
                    AssertFalse(RuntimeDetectionService.IsPiCommand(Path.Combine(directory, "missing")), "a missing command is not pi");
                }
                finally
                {
                    try { Directory.Delete(directory, true); } catch { }
                }
            }));

            cases.Add(Case("resolve_on_path_needs_an_executable", "PATH resolution finds an executable file and skips a non-executable one", TestTags.Positive, () =>
            {
                if (OperatingSystem.IsWindows()) return;
                string first = TestTemp.NewDirectory("path_a");
                string second = TestTemp.NewDirectory("path_b");
                try
                {
                    string notExecutable = Path.Combine(first, "fakecli");
                    File.WriteAllText(notExecutable, "#!/bin/sh\n");
                    string executable = Path.Combine(second, "fakecli");
                    File.WriteAllText(executable, "#!/bin/sh\n");
                    File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                    AssertEqual(executable, HarborRuntimeDetector.ResolveOnPath("fakecli", first + ":" + second, false), "the executable one is found");
                    AssertNull(HarborRuntimeDetector.ResolveOnPath("fakecli", first, false), "a file without the execute bit is not");
                    AssertNull(HarborRuntimeDetector.ResolveOnPath("missingcli", first + ":" + second, false));
                    AssertNull(HarborRuntimeDetector.ResolveOnPath("fakecli", LaunchdPath, false), "not on the launchd PATH");
                }
                finally
                {
                    TestTemp.TryDelete(first);
                    TestTemp.TryDelete(second);
                }
            }));

            return new TestSuiteDescriptor(suiteId: SuiteId, displayName: "Harbor Runtime Detection", cases: cases);
        }

        #endregion

        #region Private-Methods

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

        #endregion
    }
}
