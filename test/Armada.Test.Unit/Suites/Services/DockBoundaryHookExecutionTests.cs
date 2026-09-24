namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Test.Common;
    using Armada.Test.Unit.TestHelpers;
    using SyslogLogging;

    /// <summary>
    /// Regression suite proving that the Armada dock pre-commit hook correctly blocks staged secrets
    /// containing regex metacharacters (\s, \w, \b, embedded double-quotes) once boundary.patterns
    /// carries raw (un-JSON-escaped) patterns. Pre-fix, the hook read secret patterns from
    /// boundary.json via the extract_section JSON parser, which double-escaped backslashes so grep
    /// received \\s instead of \s and the patterns never matched.
    /// </summary>
    public class DockBoundaryHookExecutionTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Dock Boundary Hook Execution";

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("Pre-commit hook blocks secret with metachar pattern when boundary.patterns is present", async () =>
            {
                string? shPath = FindShPath();
                if (shPath == null)
                {
                    Console.WriteLine("  [SKIP] sh not found (Git for Windows bin/sh.exe or PATH sh required); hook execution test skipped");
                    return;
                }

                string tempRoot = Path.Combine(Path.GetTempPath(), "armada-hookexec-" + Guid.NewGuid().ToString("N"));

                try
                {
                    Directory.CreateDirectory(tempRoot);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = new LoggingModule();
                        logging.Settings.EnableConsole = false;

                        ArmadaSettings settings = new ArmadaSettings();
                        settings.DocksDirectory = Path.Combine(tempRoot, "docks");
                        settings.ReposDirectory = Path.Combine(tempRoot, "repos");
                        Directory.CreateDirectory(settings.DocksDirectory);
                        Directory.CreateDirectory(settings.ReposDirectory);

                        // repoDir serves as the "bare repo" location for hook installation.
                        // It is not a real git repo so InstallBoundaryHooksAsync falls back to
                        // repoDir/hooks/ as the hooks directory.
                        string repoDir = Path.Combine(settings.ReposDirectory, "hooktest.git");
                        Directory.CreateDirectory(repoDir);

                        HookRealWorktreeGitService gitService = new HookRealWorktreeGitService();
                        DockService dockService = new DockService(logging, testDb.Driver, settings, gitService);

                        Vessel vessel = new Vessel("hooktest-vessel", "https://github.com/test/repo.git");
                        vessel.LocalPath = repoDir;
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = new Captain("hooktest-captain");
                        captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        Dock? dock = await dockService.ProvisionAsync(vessel, captain, "armada/hooktest/msn_hooktest", "msn_hooktest").ConfigureAwait(false);
                        AssertNotNull(dock, "Dock must be provisioned");

                        string hookPath = Path.Combine(repoDir, "hooks", "pre-commit");
                        Assert(File.Exists(hookPath), "pre-commit hook must be installed to repoDir/hooks/pre-commit");

                        string worktreePath = dock!.WorktreePath!;
                        string patternsPath = Path.Combine(worktreePath, ".armada", "boundary.patterns");
                        Assert(File.Exists(patternsPath), "boundary.patterns must be written by WriteBoundaryConfigAsync via ProvisionAsync");

                        string patternsContent = await File.ReadAllTextAsync(patternsPath).ConfigureAwait(false);
                        AssertContains("# secretPatterns", patternsContent, "boundary.patterns must have # secretPatterns header");
                        AssertContains("# privateIdentifiers", patternsContent, "boundary.patterns must have # privateIdentifiers header");

                        // Core assertion: raw patterns must use single-backslash metacharacters, NOT
                        // the JSON-escaped double-backslash form that broke grep matching pre-fix.
                        Assert(patternsContent.Contains(@"\s"),
                            "boundary.patterns must contain raw \\s metachar (not JSON-escaped \\\\s)");
                        Assert(!patternsContent.Contains(@"\\s"),
                            "boundary.patterns must NOT contain JSON-escaped \\\\s -- that would break grep -qE");

                        // Stage a file whose content matches the password\s*[:=]\s*"\w{8,}" pattern.
                        // This exercises both an embedded double-quote in the pattern and \s/\w metachars.
                        string secretFile = Path.Combine(worktreePath, "config.txt");
                        await File.WriteAllTextAsync(secretFile, "password = \"SuperSecret1\"\n").ConfigureAwait(false);
                        await RunGitAsync(worktreePath, "add", "config.txt").ConfigureAwait(false);

                        string hookPathForSh = hookPath.Replace('\\', '/');
                        ProcessStartInfo hookSi = new ProcessStartInfo
                        {
                            FileName = shPath,
                            WorkingDirectory = worktreePath,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };
                        hookSi.ArgumentList.Add(hookPathForSh);

                        using (Process hookProc = new Process { StartInfo = hookSi })
                        {
                            hookProc.Start();
                            string hookOut = await hookProc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                            string hookErr = await hookProc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                            await hookProc.WaitForExitAsync().ConfigureAwait(false);

                            Assert(hookProc.ExitCode != 0,
                                "Hook must exit non-zero when metachar-bearing secret pattern matches staged content" +
                                " (ExitCode=" + hookProc.ExitCode + ", stderr=" + hookErr + ")");
                            AssertContains("BLOCKED:", hookErr,
                                "Hook must emit BLOCKED: message on stderr (stderr=" + hookErr + ")");
                            // CORE RULE 4: secret bytes must never be printed
                            Assert(!hookErr.Contains("SuperSecret1"),
                                "Hook must not print secret bytes (CORE RULE 4)");
                        }
                    }
                }
                finally
                {
                    SafeDeleteDirectory(tempRoot);
                }
            }).ConfigureAwait(false);

            await RunTest("Pre-commit hook blocks protected-path commit when boundary.json is present", async () =>
            {
                string? shPath = FindShPath();
                if (shPath == null)
                {
                    Console.WriteLine("  [SKIP] sh not found; protected-path hook execution test skipped");
                    return;
                }

                string tempRoot = Path.Combine(Path.GetTempPath(), "armada-hookprot-" + Guid.NewGuid().ToString("N"));

                try
                {
                    Directory.CreateDirectory(tempRoot);

                    using (TestDatabase testDb = await TestDatabaseHelper.CreateDatabaseAsync().ConfigureAwait(false))
                    {
                        LoggingModule logging = new LoggingModule();
                        logging.Settings.EnableConsole = false;

                        ArmadaSettings settings = new ArmadaSettings();
                        settings.DocksDirectory = Path.Combine(tempRoot, "docks");
                        settings.ReposDirectory = Path.Combine(tempRoot, "repos");
                        Directory.CreateDirectory(settings.DocksDirectory);
                        Directory.CreateDirectory(settings.ReposDirectory);

                        string repoDir = Path.Combine(settings.ReposDirectory, "prottest.git");
                        Directory.CreateDirectory(repoDir);

                        HookRealWorktreeGitService gitService = new HookRealWorktreeGitService();
                        DockService dockService = new DockService(logging, testDb.Driver, settings, gitService);

                        Vessel vessel = new Vessel("prottest-vessel", "https://github.com/test/repo.git");
                        vessel.LocalPath = repoDir;
                        vessel = await testDb.Driver.Vessels.CreateAsync(vessel).ConfigureAwait(false);

                        Captain captain = new Captain("prottest-captain");
                        captain = await testDb.Driver.Captains.CreateAsync(captain).ConfigureAwait(false);

                        Dock? dock = await dockService.ProvisionAsync(vessel, captain, "armada/hooktest/msn_prottest", "msn_prottest").ConfigureAwait(false);
                        AssertNotNull(dock, "Dock must be provisioned");

                        string hookPath = Path.Combine(repoDir, "hooks", "pre-commit");
                        Assert(File.Exists(hookPath), "pre-commit hook must be installed");

                        string worktreePath = dock!.WorktreePath!;

                        // Stage a modification to a built-in protected path (CLAUDE.md)
                        string claudeFile = Path.Combine(worktreePath, "CLAUDE.md");
                        await File.WriteAllTextAsync(claudeFile, "should be blocked\n").ConfigureAwait(false);
                        await RunGitAsync(worktreePath, "add", "CLAUDE.md").ConfigureAwait(false);

                        string hookPathForSh = hookPath.Replace('\\', '/');
                        ProcessStartInfo hookSi = new ProcessStartInfo
                        {
                            FileName = shPath,
                            WorkingDirectory = worktreePath,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };
                        hookSi.ArgumentList.Add(hookPathForSh);

                        using (Process hookProc = new Process { StartInfo = hookSi })
                        {
                            hookProc.Start();
                            string hookErr = await hookProc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                            await hookProc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                            await hookProc.WaitForExitAsync().ConfigureAwait(false);

                            Assert(hookProc.ExitCode != 0,
                                "Hook must exit non-zero when protected path CLAUDE.md is staged (stderr=" + hookErr + ")");
                            AssertContains("BLOCKED:", hookErr,
                                "Hook must emit BLOCKED: message for protected path (stderr=" + hookErr + ")");
                            AssertContains("CLAUDE.md", hookErr,
                                "Hook message must name the blocked protected path (stderr=" + hookErr + ")");
                        }
                    }
                }
                finally
                {
                    SafeDeleteDirectory(tempRoot);
                }
            }).ConfigureAwait(false);

            await RunTest("Both hooks block protected paths whose names Git would quote", async () =>
            {
                string? shPath = FindShPath();
                if (shPath == null)
                {
                    Console.WriteLine("  [SKIP] sh not found; quoted protected-path hook test skipped");
                    return;
                }

                string repo = Path.Combine(Path.GetTempPath(), "armada-hookquoted-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await InitHookRepoAsync(repo).ConfigureAwait(false);

                    // Built-in protected paths apply (no boundary.json); both names are non-ASCII.
                    string briefing = "_briefing/café.md";
                    Directory.CreateDirectory(Path.Combine(repo, "_briefing"));
                    await File.WriteAllTextAsync(Path.Combine(repo, "_briefing", "café.md"), "notes\n").ConfigureAwait(false);
                    await RunGitAsync(repo, "add", "--", briefing).ConfigureAwait(false);

                    HookRun commit = await RunHookAsync(shPath, repo, "pre-commit", null).ConfigureAwait(false);
                    Assert(commit.ExitCode != 0, "pre-commit must block the staged _briefing file (stderr=" + commit.StandardError + ")");
                    AssertContains("BLOCKED: commit modifies protected path '" + briefing + "'", commit.StandardError);

                    await RunGitAsync(repo, "reset", "-q").ConfigureAwait(false);
                    File.Delete(Path.Combine(repo, "_briefing", "café.md"));

                    string nested = "src/Café/CLAUDE.md";
                    Directory.CreateDirectory(Path.Combine(repo, "src", "Café"));
                    await File.WriteAllTextAsync(Path.Combine(repo, "src", "Café", "CLAUDE.md"), "rules\n").ConfigureAwait(false);
                    await RunGitAsync(repo, "add", "--", nested).ConfigureAwait(false);
                    await RunGitAsync(repo, "-c", "user.name=Hook Test", "-c", "user.email=hook@example.invalid", "commit", "-q", "--no-verify", "-m", "fixture").ConfigureAwait(false);
                    string head = await RunGitAsync(repo, "rev-parse", "HEAD").ConfigureAwait(false);
                    string parent = await RunGitAsync(repo, "rev-parse", "HEAD~1").ConfigureAwait(false);
                    string zero = new string('0', 40);

                    HookRun newBranch = await RunHookAsync(shPath, repo, "pre-push",
                        "refs/heads/work " + head + " refs/heads/work " + zero + "\n").ConfigureAwait(false);
                    Assert(newBranch.ExitCode != 0, "pre-push of a new branch must block the nested CLAUDE.md (stderr=" + newBranch.StandardError + ")");
                    AssertContains("BLOCKED: push modifies protected path '" + nested + "'", newBranch.StandardError);

                    HookRun update = await RunHookAsync(shPath, repo, "pre-push",
                        "refs/heads/work " + head + " refs/heads/work " + parent + "\n").ConfigureAwait(false);
                    Assert(update.ExitCode != 0, "pre-push of a branch update must block the nested CLAUDE.md (stderr=" + update.StandardError + ")");
                    AssertContains("BLOCKED: push modifies protected path '" + nested + "'", update.StandardError);
                }
                finally
                {
                    SafeDeleteDirectory(repo);
                }
            }).ConfigureAwait(false);

            await RunTest("Both hooks scan an added line whose content starts with ++", async () =>
            {
                string? shPath = FindShPath();
                if (shPath == null)
                {
                    Console.WriteLine("  [SKIP] sh not found; added-line hook test skipped");
                    return;
                }

                string repo = Path.Combine(Path.GetTempPath(), "armada-hookplus-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await InitHookRepoAsync(repo).ConfigureAwait(false);

                    // The diff shows this added line as "+++ -----BEGIN ...", the same prefix as a file header.
                    string keyHeader = "-----BEGIN " + "RSA PRIVATE" + " KEY-----";
                    await File.WriteAllTextAsync(Path.Combine(repo, "notes.txt"), "++ " + keyHeader + "\n").ConfigureAwait(false);
                    await RunGitAsync(repo, "add", "notes.txt").ConfigureAwait(false);

                    HookRun commit = await RunHookAsync(shPath, repo, "pre-commit", null).ConfigureAwait(false);
                    Assert(commit.ExitCode != 0, "pre-commit must scan the ++ line (stderr=" + commit.StandardError + ")");
                    AssertContains("BLOCKED: staged changes contain secret material", commit.StandardError);

                    await RunGitAsync(repo, "-c", "user.name=Hook Test", "-c", "user.email=hook@example.invalid", "commit", "-q", "--no-verify", "-m", "fixture").ConfigureAwait(false);
                    string head = await RunGitAsync(repo, "rev-parse", "HEAD").ConfigureAwait(false);
                    string parent = await RunGitAsync(repo, "rev-parse", "HEAD~1").ConfigureAwait(false);

                    HookRun push = await RunHookAsync(shPath, repo, "pre-push",
                        "refs/heads/work " + head + " refs/heads/work " + parent + "\n").ConfigureAwait(false);
                    Assert(push.ExitCode != 0, "pre-push must scan the ++ line (stderr=" + push.StandardError + ")");
                    AssertContains("BLOCKED: pushed commits contain secret material", push.StandardError);
                }
                finally
                {
                    SafeDeleteDirectory(repo);
                }
            }).ConfigureAwait(false);

            await RunTest("Hooks do not read a file header as an added line", async () =>
            {
                string? shPath = FindShPath();
                if (shPath == null)
                {
                    Console.WriteLine("  [SKIP] sh not found; header hook test skipped");
                    return;
                }

                string repo = Path.Combine(Path.GetTempPath(), "armada-hookheader-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await InitHookRepoAsync(repo).ConfigureAwait(false);

                    // A private-identifier pattern that matches only the file name: it appears in the
                    // "+++ b/<name>" header and in no added line.
                    Directory.CreateDirectory(Path.Combine(repo, ".armada"));
                    await File.WriteAllTextAsync(Path.Combine(repo, ".armada", "boundary.patterns"),
                        "# secretPatterns\n# privateIdentifiers\nheaderonlyname\n").ConfigureAwait(false);
                    await File.WriteAllTextAsync(Path.Combine(repo, "headeronlyname.txt"), "plain\n").ConfigureAwait(false);
                    await RunGitAsync(repo, "add", "headeronlyname.txt").ConfigureAwait(false);

                    HookRun commit = await RunHookAsync(shPath, repo, "pre-commit", null).ConfigureAwait(false);
                    AssertEqual(0, commit.ExitCode, "a name in a file header is not added content (stderr=" + commit.StandardError + ")");
                }
                finally
                {
                    SafeDeleteDirectory(repo);
                }
            }).ConfigureAwait(false);
        }

        #region Private-Methods

        /// <summary>
        /// Locate sh from Git for Windows or the system PATH. Returns null when sh is unavailable
        /// so tests can skip cleanly rather than false-failing on minimal CI environments.
        /// </summary>
        private static string? FindShPath()
        {
            string[] candidates = new string[]
            {
                @"C:\Program Files\Git\bin\sh.exe",
                @"C:\Program Files (x86)\Git\bin\sh.exe",
                @"C:\Git\bin\sh.exe",
                @"C:\git\bin\sh.exe",
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }

            // Try sh from PATH
            try
            {
                ProcessStartInfo si = new ProcessStartInfo
                {
                    FileName = "sh",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(si)!)
                {
                    p.WaitForExit(3000);
                    if (p.ExitCode == 0) return "sh";
                }
            }
            catch { }

            return null;
        }

        private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (string arg in args)
                startInfo.ArgumentList.Add(arg);

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                if (process.ExitCode != 0)
                    throw new InvalidOperationException("git " + String.Join(" ", args) + " failed: " + stderr.Trim());

                return stdout.Trim();
            }
        }

        private static async Task InitHookRepoAsync(string repo)
        {
            Directory.CreateDirectory(repo);
            await RunGitAsync(repo, "init", "-q", "-b", "main").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repo, "README.txt"), "base\n").ConfigureAwait(false);
            await RunGitAsync(repo, "add", "README.txt").ConfigureAwait(false);
            await RunGitAsync(repo, "-c", "user.name=Hook Test", "-c", "user.email=hook@example.invalid", "commit", "-q", "--no-verify", "-m", "base").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repo, "pre-commit"), DockService.PreCommitHookScript).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repo, "pre-push"), DockService.PrePushHookScript).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(repo, ".git", "info", "exclude"), "pre-commit\npre-push\n").ConfigureAwait(false);
        }

        private static async Task<HookRun> RunHookAsync(string shPath, string repo, string hook, string? stdin)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = shPath,
                WorkingDirectory = repo,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(hook);
            if (hook == "pre-push")
            {
                startInfo.ArgumentList.Add("origin");
                startInfo.ArgumentList.Add("https://example.invalid/repo.git");
            }

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                if (stdin != null) await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                process.StandardInput.Close();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().ConfigureAwait(false);
                return new HookRun { ExitCode = process.ExitCode, StandardOutput = await stdout.ConfigureAwait(false), StandardError = await stderr.ConfigureAwait(false) };
            }
        }

        private static void SafeDeleteDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(path, true);
            }
            catch { }
        }

        private sealed class HookRun
        {
            public int ExitCode { get; set; }

            public string StandardOutput { get; set; } = "";

            public string StandardError { get; set; } = "";
        }

        /// <summary>
        /// Git service stub that creates a real initialized git repository in the worktree path
        /// so that git staging commands (git diff --cached) work correctly when the hook executes.
        /// All other methods are no-ops or return safe defaults.
        /// </summary>
        private sealed class HookRealWorktreeGitService : IGitService
        {
            /// <inheritdoc />
            public Task CloneBareAsync(string repoUrl, string localPath, CancellationToken token = default)
            {
                Directory.CreateDirectory(localPath);
                return Task.CompletedTask;
            }

            /// <inheritdoc />
            public async Task CreateWorktreeAsync(string repoPath, string worktreePath, string branchName, string baseBranch = "main", bool detached = false, CancellationToken token = default)
            {
                Directory.CreateDirectory(worktreePath);
                await RunGitAsync(worktreePath, "init", "-b", "main").ConfigureAwait(false);
                await RunGitAsync(worktreePath, "config", "user.name", "Armada Tests").ConfigureAwait(false);
                await RunGitAsync(worktreePath, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);
                // Initial commit required so git diff --cached has a base to diff against
                await File.WriteAllTextAsync(Path.Combine(worktreePath, ".gitkeep"), "init\n").ConfigureAwait(false);
                await RunGitAsync(worktreePath, "add", ".gitkeep").ConfigureAwait(false);
                await RunGitAsync(worktreePath, "commit", "-m", "init").ConfigureAwait(false);
            }

            /// <inheritdoc />
            public Task RemoveWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task FetchAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task PushBranchAsync(string worktreePath, string remoteName = "origin", CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<string> CreatePullRequestAsync(string worktreePath, string title, string body, CancellationToken token = default) => Task.FromResult(String.Empty);

            /// <inheritdoc />
            public Task RepairWorktreeAsync(string worktreePath, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<bool> IsRepositoryAsync(string path, CancellationToken token = default) => Task.FromResult(Directory.Exists(path));

            /// <inheritdoc />
            public Task DeleteLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default, [System.Runtime.CompilerServices.CallerMemberName] string caller = "") => Task.CompletedTask;

            /// <inheritdoc />
            public Task DeleteRemoteBranchAsync(string repoPath, string branchName, CancellationToken token = default, [System.Runtime.CompilerServices.CallerMemberName] string caller = "") => Task.CompletedTask;

            /// <inheritdoc />
            public Task PushRefSpecAsync(string repoPath, string srcRef, string destRef, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<string> GetRepositoryHeadRefAsync(string repoPath, CancellationToken token = default) => Task.FromResult("refs/heads/main");

            /// <inheritdoc />
            public Task SetRepositoryHeadAsync(string repoPath, string branchName, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task PruneWorktreesAsync(string repoPath, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task EnableAutoMergeAsync(string worktreePath, string prUrl, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task MergeBranchLocalAsync(string targetWorkDir, string sourceRepoPath, string branchName, string? targetBranch = null, string? commitMessage = null, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task PullAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task PullFastForwardOnlyAsync(string workingDirectory, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<string?> GetCurrentBranchAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult<string?>(null);

            /// <inheritdoc />
            public Task<bool> IsWorkingDirectoryCleanAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(true);
            public Task<bool> HasUncommittedTrackedChangesAsync(string workingDirectory, CancellationToken token = default) => Task.FromResult(false);

            /// <inheritdoc />
            public Task<string> DiffAsync(string worktreePath, string baseBranch = "main", CancellationToken token = default) => Task.FromResult(String.Empty);

            /// <inheritdoc />
            public Task<string?> GetHeadCommitHashAsync(string worktreePath, CancellationToken token = default) => Task.FromResult<string?>("abc123hooktest");

            /// <inheritdoc />
            public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string worktreePath, string startCommit, CancellationToken token = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

            /// <inheritdoc />
            public Task<bool> IsPrMergedAsync(string workingDirectory, string prUrl, CancellationToken token = default) => Task.FromResult(false);

            /// <inheritdoc />
            public Task<bool> BranchExistsAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(false);

            /// <inheritdoc />
            public Task<bool> EnsureLocalBranchAsync(string repoPath, string branchName, CancellationToken token = default) => Task.FromResult(true);

            /// <inheritdoc />
            public Task<bool> IsWorktreeRegisteredAsync(string repoPath, string worktreePath, CancellationToken token = default) => Task.FromResult(Directory.Exists(worktreePath));

            /// <inheritdoc />
            public Task SetHeadSymbolicRefAsync(string repoPath, string targetRef, CancellationToken token = default) => Task.CompletedTask;

            /// <inheritdoc />
            public Task<IReadOnlyList<string>> GetConflictedFilesAsync(string worktreePath, CancellationToken token = default)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<int> GetCommitCountBetweenAsync(string repoPath, string fromRef, string toRef, CancellationToken token = default) => Task.FromResult(0);
        }

        #endregion
    }
}
