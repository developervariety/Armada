namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Tests the bounded native process path used by the self-deploy Release build.
    /// </summary>
    public sealed class SelfDeployBuildRunnerTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Self Deploy Build Runner";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("StubDotnet_SuccessUsesArgumentList", async () =>
            {
                if (SkipWindows("StubDotnet_SuccessUsesArgumentList")) return;
                using StubDotnetFixture fixture = StubDotnetFixture.Create("success");
                using EnvironmentScope environment = fixture.Activate();
                SelfDeployBuildResult result = await BuildAsync(fixture.DirectoryPath);
                AssertTrue(result.Succeeded, "successful stub build must pass");
                AssertEqual(0, result.ExitCode, "successful stub exit code");
                AssertContains("-c", result.OutputTail, "build configuration argument reaches stub");
                AssertContains("-f", result.OutputTail, "target framework argument reaches stub");
            });

            await RunTest("StubDotnet_NonzeroExitPreservesFailureTail", async () =>
            {
                if (SkipWindows("StubDotnet_NonzeroExitPreservesFailureTail")) return;
                using StubDotnetFixture fixture = StubDotnetFixture.Create("nonzero");
                using EnvironmentScope environment = fixture.Activate();
                SelfDeployBuildResult result = await BuildAsync(fixture.DirectoryPath);
                AssertFalse(result.Succeeded, "nonzero stub build must fail");
                AssertEqual(7, result.ExitCode, "nonzero stub exit code");
                AssertContains("stub build failed", result.OutputTail, "stderr remains in output tail");
            });

            await RunTest("StubDotnet_FloodedOutputIsBoundedAndMarked", async () =>
            {
                if (SkipWindows("StubDotnet_FloodedOutputIsBoundedAndMarked")) return;
                using StubDotnetFixture fixture = StubDotnetFixture.Create("flood");
                using EnvironmentScope environment = fixture.Activate();
                SelfDeployBuildResult result = await BuildAsync(fixture.DirectoryPath);
                AssertTrue(result.Succeeded, "flood stub exits successfully");
                AssertContains(SelfDeployNativeCommandRunner.OutputTruncationMarker, result.OutputTail,
                    "flooded output is marked as incomplete evidence");
                AssertTrue(result.OutputTail.Length <= 4096
                    + SelfDeployNativeCommandRunner.OutputTruncationMarker.Length + 1,
                    "build output tail remains bounded");
            });

            await RunTest("StubDotnet_ConfiguredTimeoutTerminatesChild", async () =>
            {
                if (SkipWindows("StubDotnet_ConfiguredTimeoutTerminatesChild")) return;
                using StubDotnetFixture fixture = StubDotnetFixture.Create("hang");
                using EnvironmentScope environment = fixture.Activate();
                SelfDeploySettings settings = CreateSettings();
                SetBuildTimeoutForFixture(settings, 1);
                SelfDeployBuildRunner runner = new SelfDeployBuildRunner(CreateLogging());
                SelfDeployBuildResult result = await runner.BuildAsync(fixture.DirectoryPath, settings);
                AssertFalse(result.Succeeded, "timed out stub build must fail");
                AssertEqual(-1, result.ExitCode, "timeout exit code");
                AssertContains("timed out", result.OutputTail, "timeout reason is stable");
                int childPid = await fixture.ReadChildPidAsync();
                AssertFalse(await WaitForProcessAsync(childPid, TimeSpan.FromSeconds(2)),
                    "timeout must leave no surviving child process");
            });

            await RunTest("StubDotnet_CallerCancellationTerminatesChild", async () =>
            {
                if (SkipWindows("StubDotnet_CallerCancellationTerminatesChild")) return;
                using StubDotnetFixture fixture = StubDotnetFixture.Create("hang");
                using EnvironmentScope environment = fixture.Activate();
                SelfDeployBuildRunner runner = new SelfDeployBuildRunner(CreateLogging());
                using CancellationTokenSource cancellation = new CancellationTokenSource();
                Task<SelfDeployBuildResult> buildTask = runner.BuildAsync(
                    fixture.DirectoryPath, CreateSettings(), cancellation.Token);
                int childPid = await fixture.ReadChildPidAsync();
                cancellation.Cancel();
                bool canceled = false;
                try
                {
                    await buildTask;
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }
                AssertTrue(canceled, "caller cancellation must propagate");
                AssertFalse(await WaitForProcessAsync(childPid, TimeSpan.FromSeconds(2)),
                    "caller cancellation must leave no surviving child process");
            });
        }

        private bool SkipWindows(string testName)
        {
            if (!OperatingSystem.IsWindows()) return false;
            SkipTest(testName, "The executable fixture uses a Unix shell and is unavailable on Windows.");
            return true;
        }

        private static SelfDeploySettings CreateSettings()
        {
            return new SelfDeploySettings
            {
                SolutionRelativePath = "src/Armada.sln",
                BuildConfiguration = "Release",
                TargetFramework = "net10.0"
            };
        }

        private static void SetBuildTimeoutForFixture(SelfDeploySettings settings, int seconds)
        {
            FieldInfo field = typeof(SelfDeploySettings).GetField("_BuildTimeoutSeconds", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Build timeout backing field was not found.");
            field.SetValue(settings, seconds);
        }

        private static async Task<SelfDeployBuildResult> BuildAsync(string workingDirectory)
        {
            return await new SelfDeployBuildRunner(CreateLogging())
                .BuildAsync(workingDirectory, CreateSettings()).ConfigureAwait(false);
        }

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        private static async Task<bool> WaitForProcessAsync(int processId, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!IsProcessAlive(processId)) return false;
                await Task.Delay(50).ConfigureAwait(false);
            }
            return IsProcessAlive(processId);
        }

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private sealed class StubDotnetFixture : IDisposable
        {
            private StubDotnetFixture(string directoryPath, string childPidPath)
            {
                DirectoryPath = directoryPath;
                ChildPidPath = childPidPath;
            }

            public string DirectoryPath { get; }
            private string ChildPidPath { get; }

            public static StubDotnetFixture Create(string mode)
            {
                string root = Path.Combine(Path.GetTempPath(), "armada-self-build-" + Guid.NewGuid().ToString("N"));
                string bin = Path.Combine(root, "bin");
                Directory.CreateDirectory(Path.Combine(root, "src"));
                File.WriteAllText(Path.Combine(root, "src", "Armada.sln"), "fixture");
                Directory.CreateDirectory(bin);
                string childPidPath = Path.Combine(root, "child.pid");
                string scriptPath = Path.Combine(bin, "dotnet");
                File.WriteAllText(scriptPath,
                    "#!/bin/sh\n" +
                    "case \"$ARMADA_STUB_MODE\" in\n" +
                    "success) printf '%s\\n' \"$@\"; exit 0 ;;\n" +
                    "nonzero) echo 'stub build failed' >&2; exit 7 ;;\n" +
                    "flood) yes output | head -c 400000; exit 0 ;;\n" +
                    "hang) (sleep 30) & child=$!; echo \"$child\" > \"$ARMADA_STUB_CHILD_PID_FILE\"; wait \"$child\" ;;\n" +
                    "*) echo 'unknown stub mode' >&2; exit 9 ;;\n" +
                    "esac\n");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                return new StubDotnetFixture(root, childPidPath) { Mode = mode };
            }

            private string Mode { get; init; } = String.Empty;

            public EnvironmentScope Activate()
            {
                return new EnvironmentScope(new[]
                {
                    new KeyValuePair<string, string>("PATH", Path.Combine(DirectoryPath, "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")),
                    new KeyValuePair<string, string>("ARMADA_STUB_MODE", Mode),
                    new KeyValuePair<string, string>("ARMADA_STUB_CHILD_PID_FILE", ChildPidPath)
                });
            }

            public async Task<int> ReadChildPidAsync()
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                while (!File.Exists(ChildPidPath) && DateTime.UtcNow < deadline)
                    await Task.Delay(25).ConfigureAwait(false);
                return Int32.Parse(await File.ReadAllTextAsync(ChildPidPath).ConfigureAwait(false));
            }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
                }
                catch (Exception)
                {
                    // The child-process assertion reports a failure; cleanup is best effort after it.
                }
            }
        }

        private sealed class EnvironmentScope : IDisposable
        {
            private readonly Dictionary<string, string?> _Previous = new Dictionary<string, string?>(StringComparer.Ordinal);

            public EnvironmentScope(IEnumerable<KeyValuePair<string, string>> values)
            {
                foreach (KeyValuePair<string, string> value in values)
                {
                    _Previous[value.Key] = Environment.GetEnvironmentVariable(value.Key);
                    Environment.SetEnvironmentVariable(value.Key, value.Value);
                }
            }

            public void Dispose()
            {
                foreach (KeyValuePair<string, string?> value in _Previous)
                    Environment.SetEnvironmentVariable(value.Key, value.Value);
            }
        }
    }
}
