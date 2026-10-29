namespace Test.Shared.Infrastructure
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;
    using static Test.Shared.Infrastructure.Asserts;

    /// <summary>Runs the public remote gate with hermetic command boundaries, without an SDK or a full disk.</summary>
    internal static class ServerGateScriptFixture
    {
        internal static async Task VerifyDiskAdmissionAsync(string repositoryRoot)
        {
            if (!OperatingSystem.IsLinux()) return;
            foreach (string scenario in new[] { "queued", "low-before", "low-after", "caller-lock", "invalid", "df-error", "boundary" })
            {
                string root = Path.Combine(Path.GetTempPath(), "gate-disk-contract-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try { await VerifyAsync(repositoryRoot, root, scenario).ConfigureAwait(false); }
                finally { Directory.Delete(root, true); }
            }
        }

        private static async Task VerifyAsync(string repositoryRoot, string root, string scenario)
        {
            string bin = Path.Combine(root, "bin");
            string home = Path.Combine(root, "home");
            string checkout = Path.Combine(root, "checkout");
            string scratch = Path.Combine(root, "scratch");
            string hostLock = Path.Combine(root, "host.lock");
            string calls = Path.Combine(root, "calls");
            foreach (string dir in new[] { bin, home, Path.Combine(checkout, "scripts", "linux") }) Directory.CreateDirectory(dir);
            string script = Path.Combine(checkout, "scripts", "linux", "server-gate.sh");
            File.Copy(Path.Combine(repositoryRoot, "scripts", "linux", "server-gate.sh"), script);
            WriteExecutable(bin, "git", """
                #!/bin/bash
                printf 'git %s\n' "$*" >> "$FIXTURE_CALLS"
                case "$1" in
                  update-index|diff|push|fetch|clean) exit 0 ;;
                  checkout) mkdir -p scripts/common; cp "$FIXTURE_ROOT/runner" scripts/common/run-tests.sh; chmod 700 scripts/common/run-tests.sh ;;
                  rev-parse) printf '%040d\n' 1 ;;
                  log) printf 'fixture subject\n' ;;
                  init) dest="${!#}"; mkdir -p "$dest"; case "$*" in *--bare*) ;; *) mkdir -p "$dest/.git" ;; esac ;;
                  -C) exit 0 ;;
                  *) printf 'unexpected git command\n' >&2; exit 97 ;;
                esac
                """);
            WriteExecutable(bin, "ssh", """
                #!/bin/bash
                [ "$1" = fixture ] || exit 98
                shift
                exec /bin/bash -c "$1"
                """);
            WriteExecutable(bin, "sleep", """
                #!/bin/bash
                if [ "$FIXTURE_SCENARIO" = queued ]; then exec /bin/sleep 0.05; fi
                exec /bin/sleep "$@"
                """);
            WriteExecutable(bin, "rmdir", """
                #!/bin/bash
                /usr/bin/rmdir "$@" || exit $?
                if [ "$FIXTURE_SCENARIO" = queued ] && [ "$FIXTURE_ROLE" = first ] && [ "$1" = "$FIXTURE_ROOT/host.lock" ]; then
                  for ((attempt=0; attempt<200; attempt++)); do
                    [ ! -f "$FIXTURE_ROOT/queued-scratch-checked" ] || { printf 'queued scratch handoff observed\n'; exit 0; }
                    /bin/sleep 0.05
                  done
                  printf 'queued scratch handoff barrier timed out\n' >&2; exit 99
                fi
                """);
            WriteExecutable(bin, "flock", """
                #!/bin/bash
                /usr/bin/flock "$@"
                status=$?
                if [ "$FIXTURE_SCENARIO" = queued ] && [ "$FIXTURE_ROLE" = second ] && [ "$*" = '-n 9' ]; then
                  touch "$FIXTURE_ROOT/queued-scratch-checked"
                fi
                exit "$status"
                """);
            WriteExecutable(bin, "df", """
                #!/bin/bash
                count=0
                [ ! -f "$FIXTURE_ROOT/df-count" ] || read -r count < "$FIXTURE_ROOT/df-count"
                count=$((count+1)); printf '%s\n' "$count" > "$FIXTURE_ROOT/df-count"
                held=no; [ ! -d "$FIXTURE_ROOT/host.lock" ] || held=yes
                printf 'df %s held=%s args=%s\n' "$count" "$held" "$*" >> "$FIXTURE_CALLS"
                if [ "$count" -gt 1 ]; then
                  if flock -n "$FIXTURE_ROOT/scratch/gate.lock" true; then scratch_lock=free; else scratch_lock=held; fi
                  printf 'df-scratch-lock=%s\n' "$scratch_lock" >> "$FIXTURE_CALLS"
                fi
                case "$FIXTURE_SCENARIO" in
                  invalid) printf 'not filesystem data\n'; exit 0 ;;
                  df-error) exit 1 ;;
                  low-before) available=$((7999*1024)) ;;
                  low-after|caller-lock) if [ "$count" = 1 ]; then available=$((8000*1024)); else available=$((7999*1024)); fi ;;
                  *) available=$((8000*1024)) ;;
                esac
                printf 'Filesystem 1024-blocks Used Available Capacity Mounted on\nfixture 99999999 1 %s 1%% /\n' "$available"
                """);
            WriteExecutable(bin, "dotnet", """
                #!/bin/bash
                printf 'dotnet %s\n' "$*" >> "$FIXTURE_CALLS"
                if [ "$1" = build ] && [ "$FIXTURE_SCENARIO" = queued ]; then
                  if [ "$FIXTURE_ROLE" = first ]; then
                    touch "$FIXTURE_ROOT/build-ready"
                    while [ ! -f "$FIXTURE_ROOT/release-build" ]; do /bin/sleep 0.05; done
                  fi
                  if ! printf 'build temp' > "$TMPDIR/build-probe"; then
                    printf ' error missing gate temp directory\n'; exit 1
                  fi
                fi
                case "$1" in --version) printf '10.0.fixture\n';; build) printf 'Build succeeded.\n';; build-server) exit 0;; *) exit 96;; esac
                """);
            WriteExecutable(root, "runner", """
                #!/bin/bash
                printf 'tests\n' >> "$FIXTURE_CALLS"
                printf 'RESULT: PASS\n'
                """);
            string sibling = Path.Combine(root, "foreign-proof");
            File.WriteAllText(sibling, "foreign evidence");
            string? tempSentinel = null;
            string? logSentinel = null;
            if (scenario != "low-before")
            {
                Directory.CreateDirectory(Path.Combine(scratch, "worktree", ".git"));
                string runnerDir = Path.Combine(scratch, "worktree", "scripts", "common");
                Directory.CreateDirectory(runnerDir);
                WriteExecutable(runnerDir, "run-tests.sh", """
                    #!/bin/bash
                    printf 'tests\n' >> "$FIXTURE_CALLS"
                    printf 'RESULT: PASS\n'
                    """);
                Directory.CreateDirectory(Path.Combine(scratch, "tmp"));
                Directory.CreateDirectory(Path.Combine(scratch, "logs", "prior-proof"));
                tempSentinel = Path.Combine(scratch, "tmp", "preserve");
                logSentinel = Path.Combine(scratch, "logs", "prior-proof", "preserve");
                File.WriteAllText(tempSentinel, "owned but not admitted");
                File.WriteAllText(logSentinel, "prior proof");
            }
            string? callerOwner = null;
            if (scenario == "caller-lock")
            {
                Directory.CreateDirectory(hostLock);
                callerOwner = Path.Combine(hostLock, "owner");
                File.WriteAllText(callerOwner, "pid=foreign\nscratch=foreign\n");
            }
            ProcessStartInfo start = new("/bin/bash") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string arg in new[] { script, "HEAD", "--ssh-host", "fixture", "--scratch-dir", scratch, "--host-lock", hostLock, "--min-available-mb", "0" }) start.ArgumentList.Add(arg);
            if (scenario == "caller-lock") start.ArgumentList.Add("--lock-held");
            // The environment contract is used so the pre-fix public script reaches costly work and gives a meaningful RED.
            start.Environment["ARMADA_GATE_MIN_FREE_DISK_MB"] = "8000";
            start.Environment.Remove("DOTNET_ROOT");
            start.Environment["HOME"] = home;
            start.Environment["PATH"] = bin + ":/usr/bin:/bin";
            start.Environment["FIXTURE_ROOT"] = root;
            start.Environment["FIXTURE_CALLS"] = calls;
            start.Environment["FIXTURE_SCENARIO"] = scenario;
            if (scenario == "queued")
            {
                await VerifyQueuedAsync(start, root, scratch).ConfigureAwait(false);
                return;
            }
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("fixture process did not start");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            try
            {
                Task wait = process.WaitForExitAsync();
                if (await Task.WhenAny(wait, Task.Delay(15000)).ConfigureAwait(false) != wait)
                    throw new InvalidOperationException("server gate fixture exceeded 15 seconds: " + scenario);
                await wait.ConfigureAwait(false);
                string text = await output.ConfigureAwait(false) + await errors.ConfigureAwait(false);
                string recorded = File.Exists(calls) ? File.ReadAllText(calls) : "";
                bool success = scenario == "boundary";
                AssertEqual(success ? 0 : 1, process.ExitCode, "disk admission result for " + scenario + "\n" + text + "\n" + recorded);
                AssertEqual("foreign evidence", File.ReadAllText(sibling), "foreign proof is untouched");
                if (success)
                {
                    AssertTrue(recorded.Contains("git fetch", StringComparison.Ordinal) && recorded.Contains("dotnet build src/Armada.sln", StringComparison.Ordinal) && recorded.Contains("tests\n", StringComparison.Ordinal), "exact floor admits the full stub gate");
                    AssertTrue(text.Contains("RESULT: PASS", StringComparison.Ordinal), "the public gate result stays intact");
                }
                else
                {
                    AssertTrue(!recorded.Contains("git fetch", StringComparison.Ordinal) && !recorded.Contains("dotnet build src/Armada.sln", StringComparison.Ordinal) && !recorded.Contains("tests\n", StringComparison.Ordinal), "no checkout, build or tests after refusal\n" + text + "\n" + recorded);
                    AssertTrue(text.Contains("disk admission", StringComparison.OrdinalIgnoreCase), "refusal names the infrastructure admission failure\n" + text + "\n" + recorded);
                    AssertTrue(!recorded.Contains("dotnet build-server", StringComparison.Ordinal), "a refused gate does not shut down another gate's build servers");
                    bool afterPush = scenario is "low-after" or "caller-lock";
                    AssertEqual(afterPush, recorded.Contains("git push", StringComparison.Ordinal), "only the admitted second check may follow a push");
                    if (afterPush) AssertTrue(recorded.Contains("df 2 held=yes", StringComparison.Ordinal)
                        && recorded.Contains("df-scratch-lock=held", StringComparison.Ordinal), "space is rechecked under the host and scratch locks");
                    if (scenario == "low-before") AssertTrue(recorded.Contains("args=-Pk " + root + "\n", StringComparison.Ordinal), "absent scratch checks its nearest existing ancestor");
                    if (tempSentinel != null) AssertEqual("owned but not admitted", File.ReadAllText(tempSentinel), "refused run preserves temp sentinel");
                    if (logSentinel != null) AssertEqual("prior proof", File.ReadAllText(logSentinel), "refused run preserves prior logs");
                    if (callerOwner != null) AssertEqual("pid=foreign\nscratch=foreign\n", File.ReadAllText(callerOwner), "caller-owned lock is untouched");
                }
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
                await Task.WhenAll(output, errors).ConfigureAwait(false);
            }
        }

        private static async Task VerifyQueuedAsync(ProcessStartInfo start, string root, string scratch)
        {
            start.Environment["FIXTURE_ROLE"] = "first";
            using (Process first = Process.Start(start) ?? throw new InvalidOperationException("first gate did not start"))
            {
            Task<string> firstOutput = first.StandardOutput.ReadToEndAsync();
            Task<string> firstErrors = first.StandardError.ReadToEndAsync();
            Process? second = null;
            Task<string>? secondOutput = null;
            Task<string>? secondErrors = null;
            try
            {
                MonotonicDeadline deadline = MonotonicDeadline.After(TimeSpan.FromSeconds(15));
                while (!File.Exists(Path.Combine(root, "build-ready")))
                {
                    if (first.HasExited || deadline.Passed)
                        throw new InvalidOperationException("first gate did not reach its build barrier");
                    await Task.Delay(20).ConfigureAwait(false);
                }
                start.Environment["FIXTURE_ROLE"] = "second";
                second = Process.Start(start) ?? throw new InvalidOperationException("queued gate did not start");
                TaskCompletionSource<bool> waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
                secondOutput = ReadQueuedOutputAsync(second, waiting);
                secondErrors = second.StandardError.ReadToEndAsync();
                if (await Task.WhenAny(waiting.Task, Task.Delay(15000)).ConfigureAwait(false) != waiting.Task)
                    throw new InvalidOperationException("second gate did not wait for the first gate's host lock");
                File.WriteAllText(Path.Combine(root, "release-build"), "release");
                Task exited = Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync());
                if (await Task.WhenAny(exited, Task.Delay(45000)).ConfigureAwait(false) != exited)
                    throw new InvalidOperationException("queued gates did not finish");
                await exited.ConfigureAwait(false);
                string firstText = await firstOutput.ConfigureAwait(false) + await firstErrors.ConfigureAwait(false);
                string secondText = await secondOutput.ConfigureAwait(false) + await secondErrors.ConfigureAwait(false);
                AssertTrue(firstText.Contains("queued scratch handoff observed", StringComparison.Ordinal)
                    && !firstText.Contains("queued scratch handoff barrier timed out", StringComparison.Ordinal), "the first gate stays alive until the queued scratch-lock attempt\n" + firstText);
                AssertEqual(0, first.ExitCode, "first gate succeeds before handing off its scratch directory\n" + firstText);
                AssertEqual(0, second.ExitCode, "queued gate owns its scratch lock and build temp after the holder cleans up\n" + secondText);
                AssertTrue(!Directory.Exists(Path.Combine(scratch, "tmp")), "the final holder cleans up its own temp");
            }
            finally
            {
                File.WriteAllText(Path.Combine(root, "release-build"), "release");
                File.WriteAllText(Path.Combine(root, "queued-scratch-checked"), "release");
                foreach (Process? process in new[] { first, second })
                {
                    if (process != null && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                }
                await Task.WhenAll(firstOutput, firstErrors).ConfigureAwait(false);
                if (secondOutput != null && secondErrors != null)
                    await Task.WhenAll(secondOutput, secondErrors).ConfigureAwait(false);
                second?.Dispose();
            }
        }

        }

        private static async Task<string> ReadQueuedOutputAsync(Process process, TaskCompletionSource<bool> waiting)
        {
            System.Text.StringBuilder output = new();
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is string line)
            {
                output.AppendLine(line);
                if (line.Contains("Host lock: waiting", StringComparison.Ordinal)) waiting.TrySetResult(true);
            }
            return output.ToString();
        }

        private static void WriteExecutable(string directory, string name, string contents)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllText(path, contents + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
