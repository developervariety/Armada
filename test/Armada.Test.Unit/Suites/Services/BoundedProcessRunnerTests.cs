namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Test.Common;

    /// <summary>
    /// Tests the shared runner for short-lived child processes: concurrent drain, timeout and cancellation kills,
    /// bounded drain after exit, and named truncation.
    /// </summary>
    public sealed class BoundedProcessRunnerTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Bounded Process Runner";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Each stream is returned exactly, line endings included", async () =>
            {
                if (SkipOnWindows("Each stream is returned exactly, line endings included")) return;
                BoundedProcessResult result = await RunShellAsync("printf 'a\\r\\nb\\n\\nc'; printf 'err\\r\\n' >&2; exit 3", TimeSpan.FromSeconds(20));
                AssertEqual(3, result.ExitCode ?? -1, "exit code");
                AssertEqual("a\r\nb\n\nc", result.StandardOutput, "stdout kept exactly");
                AssertEqual("err\r\n", result.StandardError, "stderr kept exactly and separately");
                AssertFalse(result.Truncated, "nothing truncated");
                AssertFalse(result.TimedOut || result.Cancelled || result.OutputDrainTimedOut, "a normal exit");
            });

            await RunTest("A child writing far more than a pipe buffer to stderr while stdout stays open does not block", async () =>
            {
                if (SkipOnWindows("A child writing far more than a pipe buffer to stderr while stdout stays open does not block")) return;
                // 1 MiB on stderr first, then stdout: a reader that finishes stdout before starting stderr waits
                // for an end of stdout that never comes, because the child is blocked writing stderr.
                BoundedProcessResult result = await RunShellAsync(
                    "head -c 1048576 /dev/zero | tr '\\0' 'e' >&2; echo done",
                    TimeSpan.FromSeconds(30));
                AssertFalse(result.TimedOut, "must not time out");
                AssertEqual(0, result.ExitCode ?? -1, "exit code");
                AssertEqual("done\n", result.StandardOutput, "stdout");
                AssertEqual(1048576, result.StandardError.Length, "all of stderr is kept within the default budget");
                AssertTrue(result.Duration < TimeSpan.FromSeconds(15), "finishes promptly, took " + result.Duration);
            });

            await RunTest("A timeout kills the process and the child it started", async () =>
            {
                if (SkipOnWindows("A timeout kills the process and the child it started")) return;
                string pidFile = TempFile();
                try
                {
                    BoundedProcessResult result = await RunShellAsync(
                        "sleep 60 & echo $! > '" + pidFile + "'; wait",
                        TimeSpan.FromMilliseconds(1500));
                    AssertTrue(result.TimedOut, "timed out");
                    AssertFalse(result.Cancelled, "a timeout is not a cancellation");
                    AssertNull(result.ExitCode, "no exit code after a kill");
                    AssertTrue(result.Duration < TimeSpan.FromSeconds(10), "returns promptly, took " + result.Duration);
                    AssertTrue(await ProcessGoneAsync(ReadPid(pidFile)), "the child sleep was killed with the tree");
                }
                finally
                {
                    Cleanup(pidFile);
                }
            });

            await RunTest("A caller cancellation kills the process and the child it started", async () =>
            {
                if (SkipOnWindows("A caller cancellation kills the process and the child it started")) return;
                string pidFile = TempFile();
                try
                {
                    using (CancellationTokenSource cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500)))
                    {
                        BoundedProcessResult result = await RunShellAsync(
                            "sleep 60 & echo $! > '" + pidFile + "'; wait",
                            TimeSpan.FromSeconds(60),
                            cancel.Token);
                        AssertTrue(result.Cancelled, "cancelled");
                        AssertFalse(result.TimedOut, "a cancellation is not a timeout");
                        AssertTrue(result.Duration < TimeSpan.FromSeconds(10), "returns promptly, took " + result.Duration);
                    }

                    AssertTrue(await ProcessGoneAsync(ReadPid(pidFile)), "the child sleep was killed with the tree");
                }
                finally
                {
                    Cleanup(pidFile);
                }
            });

            await RunTest("A timeout kills a background child that left the process tree when the run owns a process group", async () =>
            {
                if (SkipOnWindows("A timeout kills a background child that left the process tree when the run owns a process group")) return;
                if (BoundedProcessRunner.GroupLauncher == null)
                {
                    SkipTest("A timeout kills a background child that left the process tree when the run owns a process group", "No setsid or perl on this host.");
                    return;
                }

                string pidFile = TempFile();
                try
                {
                    // The subshell exits at once, so its sleep is re-parented and is no longer in the live tree.
                    BoundedProcessRequest request = new BoundedProcessRequest(Shell("(sleep 60 >/dev/null 2>&1 & echo $! > '" + pidFile + "'); sleep 60"), TimeSpan.FromMilliseconds(1500))
                    {
                        OwnProcessGroup = true
                    };
                    BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request);
                    AssertTrue(result.TimedOut, "timed out");
                    AssertTrue(await ProcessGoneAsync(ReadPid(pidFile)), "the orphaned sleep died with the process group");
                }
                finally
                {
                    Cleanup(pidFile);
                }
            });

            await RunTest("A timeout kills a background child after its shell has already exited when the run owns a process group", async () =>
            {
                if (SkipOnWindows("A timeout kills a background child after its shell has already exited when the run owns a process group")) return;
                if (BoundedProcessRunner.GroupLauncher == null)
                {
                    SkipTest("A timeout kills a background child after its shell has already exited when the run owns a process group", "No setsid or perl on this host.");
                    return;
                }

                string pidFile = TempFile();
                try
                {
                    // The shell exits at once; the child holds the output pipe, so the run times out with no live
                    // parent left to walk a tree from. Only the process group still names the child.
                    BoundedProcessRequest request = new BoundedProcessRequest(Shell("sleep 30 & echo $! > '" + pidFile + "'; echo parent-done"), TimeSpan.FromSeconds(1))
                    {
                        OwnProcessGroup = true,
                        WaitForOutputClose = true
                    };
                    BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request);
                    AssertTrue(result.TimedOut, "the held pipe keeps the run open until the timeout");
                    bool gone = await ProcessGoneAsync(ReadPid(pidFile));
                    if (!gone) KillPid(pidFile);
                    AssertTrue(gone, "the child must not survive its exited shell");
                }
                finally
                {
                    Cleanup(pidFile);
                }
            });

            await RunTest("KnownGap: a descendant that starts its own session and leaves the tree survives a group kill", async () =>
            {
                const string name = "KnownGap: a descendant that starts its own session and leaves the tree survives a group kill";
                if (SkipOnWindows(name)) return;
                if (BoundedProcessRunner.GroupLauncher == null || !OnPath("perl"))
                {
                    SkipTest(name, "No process-group launcher or no perl to start a new session on this host.");
                    return;
                }

                string pidFile = TempFile();
                try
                {
                    // The subshell exits at once, so the grandchild is re-parented out of the live tree, and it calls
                    // setsid, so it leaves the run's process group as well. Neither kill can name it. This pins the
                    // documented containment limit; a change that contains it must flip this assertion.
                    string escape = "(perl -e 'use POSIX (); POSIX::setsid(); exec \"sleep\", \"30\"' >/dev/null 2>&1 & echo $! > '" + pidFile + "'); sleep 60";
                    BoundedProcessRequest request = new BoundedProcessRequest(Shell(escape), TimeSpan.FromMilliseconds(1500))
                    {
                        OwnProcessGroup = true
                    };
                    BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request);
                    AssertTrue(result.TimedOut, "timed out");
                    int escaped = ReadPid(pidFile);
                    await Task.Delay(500);
                    AssertTrue(IsAlive(escaped), "a descendant in its own session outside the tree is not contained");
                }
                finally
                {
                    KillPid(pidFile);
                    Cleanup(pidFile);
                }
            });

            await RunTest("A background child holding the output pipe after exit cannot hang the run", async () =>
            {
                if (SkipOnWindows("A background child holding the output pipe after exit cannot hang the run")) return;
                string pidFile = TempFile();
                try
                {
                    BoundedProcessRequest request = new BoundedProcessRequest(Shell("sleep 60 & echo $! > '" + pidFile + "'; echo started"), TimeSpan.FromSeconds(60))
                    {
                        OutputDrainTimeout = TimeSpan.FromMilliseconds(500)
                    };
                    BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request);
                    AssertEqual(0, result.ExitCode ?? -1, "the process itself exited 0");
                    AssertFalse(result.TimedOut, "not a timeout");
                    AssertTrue(result.OutputDrainTimedOut, "the held pipe is reported, not hidden");
                    AssertEqual("started\n", result.StandardOutput, "output written before exit is kept");
                    AssertTrue(result.Duration < TimeSpan.FromSeconds(10), "returns within the drain bound, took " + result.Duration);
                }
                finally
                {
                    KillPid(pidFile);
                    Cleanup(pidFile);
                }
            });

            await RunTest("Output above the budget keeps its beginning and its end and names what was dropped", async () =>
            {
                if (SkipOnWindows("Output above the budget keeps its beginning and its end and names what was dropped")) return;
                BoundedProcessRequest request = new BoundedProcessRequest(Shell("i=0; while [ $i -lt 20000 ]; do echo line-$i; i=$((i+1)); done"), TimeSpan.FromSeconds(30))
                {
                    OutputLimitBytes = 16 * 1024
                };
                BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request);
                AssertEqual(0, result.ExitCode ?? -1, "exit code");
                AssertTrue(result.StandardOutputTruncated, "truncation is reported");
                AssertTrue(result.StandardOutputOmittedBytes > 100_000, "omitted bytes are counted: " + result.StandardOutputOmittedBytes);
                AssertTrue(result.StandardOutput.StartsWith("line-0\nline-1\n", StringComparison.Ordinal), "the beginning is kept");
                AssertTrue(result.StandardOutput.EndsWith("line-19999\n", StringComparison.Ordinal), "the end is kept");
                AssertContains(result.StandardOutputOmittedBytes + " bytes of output omitted", result.StandardOutput, "the marker names the omitted bytes");
                AssertFalse(result.StandardErrorTruncated, "stderr has its own budget");
            });

            await RunTest("A head-only budget keeps the beginning and places a custom marker", async () =>
            {
                if (SkipOnWindows("A head-only budget keeps the beginning and places a custom marker")) return;
                BoundedProcessRequest request = new BoundedProcessRequest(Shell("head -c 100000 /dev/zero | tr '\\0' 'x'; echo END"), TimeSpan.FromSeconds(30))
                {
                    OutputLimitBytes = 4096,
                    OutputShape = BoundedOutputShapeEnum.Head,
                    TruncationMarker = "\n[cut]"
                };
                BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request);
                AssertEqual(new string('x', 4096) + "\n[cut]", result.StandardOutput, "beginning plus marker");
                AssertEqual(100004L - 4096L, result.StandardOutputOmittedBytes, "omitted bytes");
            });

            await RunTest("Standard input is written and then closed, so a prompting child ends", async () =>
            {
                if (SkipOnWindows("Standard input is written and then closed, so a prompting child ends")) return;
                BoundedProcessRequest withInput = new BoundedProcessRequest(Shell("cat"), TimeSpan.FromSeconds(20)) { StandardInput = "hello\nworld" };
                BoundedProcessResult echoed = await BoundedProcessRunner.RunAsync(withInput);
                AssertEqual("hello\nworld", echoed.StandardOutput, "input reaches the child");

                BoundedProcessResult prompted = await RunShellAsync("read answer; echo got:$answer", TimeSpan.FromSeconds(20));
                AssertFalse(prompted.TimedOut, "a prompt reads end of file instead of waiting");
                AssertEqual("got:\n", prompted.StandardOutput, "empty answer");
            });

            await RunTest("A missing executable fails to start", async () =>
            {
                ProcessStartInfo startInfo = new ProcessStartInfo("armada-no-such-executable-" + Guid.NewGuid().ToString("N"));
                bool threw = false;
                try
                {
                    await BoundedProcessRunner.RunAsync(new BoundedProcessRequest(startInfo, TimeSpan.FromSeconds(5)));
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    threw = true;
                }
                AssertTrue(threw, "start failure surfaces as Win32Exception");
            });

            await RunTest("An already-cancelled token starts nothing", async () =>
            {
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    cancel.Cancel();
                    BoundedProcessResult result = await BoundedProcessRunner.RunAsync(
                        new BoundedProcessRequest(new ProcessStartInfo("armada-never-started"), TimeSpan.FromSeconds(5)), cancel.Token);
                    AssertTrue(result.Cancelled, "cancelled");
                    AssertNull(result.ExitCode, "no exit code");
                }
            });

            await RunTest("The git start settings turn off prompts and the pager", () =>
            {
                ProcessStartInfo startInfo = GitProcessStartInfo.Create("/tmp", new[] { "status", "--porcelain" });
                AssertEqual("git", startInfo.FileName, "file name");
                AssertEqual("status", startInfo.ArgumentList[0], "first argument");
                AssertEqual("0", startInfo.Environment["GIT_TERMINAL_PROMPT"], "terminal prompt off");
                AssertEqual("Never", startInfo.Environment["GCM_INTERACTIVE"], "credential manager prompt off");
                AssertEqual("cat", startInfo.Environment["GIT_PAGER"], "no pager");
                return Task.CompletedTask;
            });
        }

        private bool SkipOnWindows(string name)
        {
            if (!OperatingSystem.IsWindows()) return false;
            SkipTest(name, "The fixture uses a POSIX shell.");
            return true;
        }

        private static bool OnPath(string name)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (File.Exists(Path.Combine(directory, name))) return true;
            }
            return false;
        }

        private static ProcessStartInfo Shell(string script)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo("/bin/sh");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(script);
            return startInfo;
        }

        private static Task<BoundedProcessResult> RunShellAsync(string script, TimeSpan timeout, CancellationToken token = default)
        {
            return BoundedProcessRunner.RunAsync(new BoundedProcessRequest(Shell(script), timeout), token);
        }

        private static string TempFile()
        {
            return Path.Combine(Path.GetTempPath(), "armada-bounded-process-" + Guid.NewGuid().ToString("N") + ".pid");
        }

        private static int ReadPid(string pidFile)
        {
            for (int i = 0; i < 50 && !File.Exists(pidFile); i++) Thread.Sleep(20);
            return Int32.Parse(File.ReadAllText(pidFile).Trim());
        }

        private static async Task<bool> ProcessGoneAsync(int pid)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (!IsAlive(pid)) return true;
                await Task.Delay(50);
            }
            return !IsAlive(pid);
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static void KillPid(string pidFile)
        {
            if (!File.Exists(pidFile)) return;
            if (!Int32.TryParse(File.ReadAllText(pidFile).Trim(), out int pid)) return;
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    process.Kill();
                }
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }

        private static void Cleanup(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
