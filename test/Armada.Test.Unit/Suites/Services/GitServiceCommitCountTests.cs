namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using Armada.Core.Services;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Tests for GitService.GetCommitCountBetweenAsync using real local git repositories.
    /// </summary>
    public class GitServiceCommitCountTests : TestSuite
    {
        public override string Name => "Git Service Commit Count";

        private static bool IsGitOnPath()
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(info)!)
                {
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
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
            {
                startInfo.ArgumentList.Add(arg);
            }

            using (Process process = new Process { StartInfo = startInfo })
            {
                process.Start();
                string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("git failed (exit " + process.ExitCode + "): " + stderr.Trim());
                }

                return stdout.Trim();
            }
        }

        /// <summary>
        /// Recursively delete a temp directory, swallowing failures. On Windows the files under
        /// <c>.git/objects</c> are marked read-only, so a plain <see cref="Directory.Delete(string, bool)"/>
        /// throws <see cref="UnauthorizedAccessException"/>; clear the read-only bit first, then ignore
        /// any residual failure (mirrors the cleanup style in MergeQueueBranchCleanupTests).
        /// </summary>
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

        private async Task<(string RepoPath, string CommitA, string CommitB, string CommitC)> CreateRepoWithThreeCommitsAsync()
        {
            string repoPath = Path.Combine(Path.GetTempPath(), "armada_gitcount_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(repoPath);

            await RunGitAsync(repoPath, "init", "-b", "main").ConfigureAwait(false);
            await RunGitAsync(repoPath, "config", "user.name", "Armada Tests").ConfigureAwait(false);
            await RunGitAsync(repoPath, "config", "user.email", "armada-tests@example.com").ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(repoPath, "a.txt"), "A\n").ConfigureAwait(false);
            await RunGitAsync(repoPath, "add", "a.txt").ConfigureAwait(false);
            await RunGitAsync(repoPath, "commit", "-m", "Commit A").ConfigureAwait(false);
            string commitA = await RunGitAsync(repoPath, "rev-parse", "HEAD").ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(repoPath, "b.txt"), "B\n").ConfigureAwait(false);
            await RunGitAsync(repoPath, "add", "b.txt").ConfigureAwait(false);
            await RunGitAsync(repoPath, "commit", "-m", "Commit B").ConfigureAwait(false);
            string commitB = await RunGitAsync(repoPath, "rev-parse", "HEAD").ConfigureAwait(false);

            await File.WriteAllTextAsync(Path.Combine(repoPath, "c.txt"), "C\n").ConfigureAwait(false);
            await RunGitAsync(repoPath, "add", "c.txt").ConfigureAwait(false);
            await RunGitAsync(repoPath, "commit", "-m", "Commit C").ConfigureAwait(false);
            string commitC = await RunGitAsync(repoPath, "rev-parse", "HEAD").ConfigureAwait(false);

            return (repoPath, commitA, commitB, commitC);
        }

        protected override async Task RunTestsAsync()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            GitService service = new GitService(logging);

            // Argument-guard tests short-circuit before any git invocation, so they run
            // even when git is absent from PATH.
            await RunTest("GetCommitCountBetweenAsync_EmptyRepoPath_Returns0", async () =>
            {
                int count = await service.GetCommitCountBetweenAsync(String.Empty, "HEAD~1", "HEAD").ConfigureAwait(false);
                AssertEqual(0, count, "Empty repoPath should short-circuit to 0");
            }).ConfigureAwait(false);

            await RunTest("GetCommitCountBetweenAsync_EmptyFromRef_Returns0", async () =>
            {
                int count = await service.GetCommitCountBetweenAsync("/some/repo", String.Empty, "HEAD").ConfigureAwait(false);
                AssertEqual(0, count, "Empty fromRef should short-circuit to 0");
            }).ConfigureAwait(false);

            await RunTest("GetCommitCountBetweenAsync_EmptyToRef_Returns0", async () =>
            {
                int count = await service.GetCommitCountBetweenAsync("/some/repo", "HEAD", String.Empty).ConfigureAwait(false);
                AssertEqual(0, count, "Empty toRef should short-circuit to 0");
            }).ConfigureAwait(false);

            if (!IsGitOnPath())
            {
                Console.WriteLine("  SKIP  GitServiceCommitCountTests (git-backed cases) -- git not found on PATH");
                return;
            }

            await RunTest("EnsureLocalBranchAsync_BareAheadOfUpstream_DoesNotDiscardLocalCommits", async () =>
            {
                // Regression: SyncLocalBranchFromRemoteAsync used to run
                //   git branch -f <branch> refs/remotes/origin/<branch>
                // unconditionally, resetting the bare's branch to the remote and silently
                // destroying any commit that had landed locally but not reached the upstream.
                // For a LocalMerge vessel -- which lands locally and pushes only when the operator
                // chooses -- the bare is legitimately ahead of the remote nearly all the time.
                string root = Path.Combine(Path.GetTempPath(), "armada_ahead_" + Guid.NewGuid().ToString("N"));
                string upstream = Path.Combine(root, "upstream.git");
                string bare = Path.Combine(root, "bare.git");
                string work = Path.Combine(root, "work");
                Directory.CreateDirectory(root);

                try
                {
                    // Upstream with a single commit on main.
                    Directory.CreateDirectory(work);
                    await RunGitAsync(work, "init", "--initial-branch=main").ConfigureAwait(false);
                    await RunGitAsync(work, "config", "user.email", "t@example.com").ConfigureAwait(false);
                    await RunGitAsync(work, "config", "user.name", "t").ConfigureAwait(false);
                    File.WriteAllText(Path.Combine(work, "a.txt"), "a");
                    await RunGitAsync(work, "add", "-A").ConfigureAwait(false);
                    await RunGitAsync(work, "commit", "-m", "upstream commit").ConfigureAwait(false);
                    await RunGitAsync(root, "clone", "--bare", work, upstream).ConfigureAwait(false);

                    // Bare mirror of upstream, then a LOCAL-ONLY commit on top of main.
                    await RunGitAsync(root, "clone", "--bare", upstream, bare).ConfigureAwait(false);
                    await RunGitAsync(bare, "fetch", "origin", "+refs/heads/*:refs/remotes/origin/*").ConfigureAwait(false);

                    File.WriteAllText(Path.Combine(work, "b.txt"), "b");
                    await RunGitAsync(work, "add", "-A").ConfigureAwait(false);
                    await RunGitAsync(work, "commit", "-m", "local-only landing").ConfigureAwait(false);
                    string localOnly = (await RunGitAsync(work, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    await RunGitAsync(bare, "fetch", work, "main:main").ConfigureAwait(false);

                    string beforeSync = (await RunGitAsync(bare, "rev-parse", "main").ConfigureAwait(false)).Trim();
                    AssertEqual(localOnly, beforeSync, "bare main should carry the local-only commit before sync");

                    // This is the path that used to reset the branch to origin/main.
                    await service.EnsureLocalBranchAsync(bare, "main").ConfigureAwait(false);

                    string afterSync = (await RunGitAsync(bare, "rev-parse", "main").ConfigureAwait(false)).Trim();
                    AssertEqual(
                        localOnly,
                        afterSync,
                        "a bare branch ahead of its upstream must never be reset to the remote ref");
                }
                finally
                {
                    SafeDeleteDirectory(root);
                }
            }).ConfigureAwait(false);

            await RunTest("EnsureLocalBranchAsync_BareBehindUpstream_StillFastForwards", async () =>
            {
                // The guard must only protect ahead-of-upstream branches: a branch that is purely
                // behind must still sync, or docks would be cut from stale refs.
                string root = Path.Combine(Path.GetTempPath(), "armada_behind_" + Guid.NewGuid().ToString("N"));
                string upstream = Path.Combine(root, "upstream.git");
                string bare = Path.Combine(root, "bare.git");
                string work = Path.Combine(root, "work");
                Directory.CreateDirectory(root);

                try
                {
                    Directory.CreateDirectory(work);
                    await RunGitAsync(work, "init", "--initial-branch=main").ConfigureAwait(false);
                    await RunGitAsync(work, "config", "user.email", "t@example.com").ConfigureAwait(false);
                    await RunGitAsync(work, "config", "user.name", "t").ConfigureAwait(false);
                    File.WriteAllText(Path.Combine(work, "a.txt"), "a");
                    await RunGitAsync(work, "add", "-A").ConfigureAwait(false);
                    await RunGitAsync(work, "commit", "-m", "first").ConfigureAwait(false);
                    await RunGitAsync(root, "clone", "--bare", work, upstream).ConfigureAwait(false);
                    await RunGitAsync(root, "clone", "--bare", upstream, bare).ConfigureAwait(false);

                    // Advance upstream only; the bare stays behind.
                    File.WriteAllText(Path.Combine(work, "c.txt"), "c");
                    await RunGitAsync(work, "add", "-A").ConfigureAwait(false);
                    await RunGitAsync(work, "commit", "-m", "second").ConfigureAwait(false);
                    string advanced = (await RunGitAsync(work, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
                    await RunGitAsync(upstream, "fetch", work, "main:main").ConfigureAwait(false);
                    await RunGitAsync(bare, "fetch", "origin", "+refs/heads/*:refs/remotes/origin/*").ConfigureAwait(false);

                    await service.EnsureLocalBranchAsync(bare, "main").ConfigureAwait(false);

                    string afterSync = (await RunGitAsync(bare, "rev-parse", "main").ConfigureAwait(false)).Trim();
                    AssertEqual(advanced, afterSync, "a behind-only branch should still fast-forward to the remote");
                }
                finally
                {
                    SafeDeleteDirectory(root);
                }
            }).ConfigureAwait(false);

            await RunTest("GetCommitCountBetweenAsync_AtoC_Returns2", async () =>
            {
                (string repoPath, string commitA, string _, string commitC) = await CreateRepoWithThreeCommitsAsync().ConfigureAwait(false);
                try
                {
                    int count = await service.GetCommitCountBetweenAsync(repoPath, commitA, commitC).ConfigureAwait(false);
                    AssertEqual(2, count, "A..C should be 2 commits ahead");
                }
                finally
                {
                    SafeDeleteDirectory(repoPath);
                }
            }).ConfigureAwait(false);

            await RunTest("GetCommitCountBetweenAsync_SameRef_Returns0", async () =>
            {
                (string repoPath, string _, string __, string commitC) = await CreateRepoWithThreeCommitsAsync().ConfigureAwait(false);
                try
                {
                    int count = await service.GetCommitCountBetweenAsync(repoPath, commitC, commitC).ConfigureAwait(false);
                    AssertEqual(0, count, "C..C should be 0 commits ahead");
                }
                finally
                {
                    SafeDeleteDirectory(repoPath);
                }
            }).ConfigureAwait(false);

            await RunTest("GetCommitCountBetweenAsync_InvalidRef_Returns0WithoutThrowing", async () =>
            {
                (string repoPath, string _, string __, string ___) = await CreateRepoWithThreeCommitsAsync().ConfigureAwait(false);
                try
                {
                    int count = await service.GetCommitCountBetweenAsync(repoPath, "deadbeefdeadbeef", "cafecafecafecafe").ConfigureAwait(false);
                    AssertEqual(0, count, "Invalid refs should return 0 without throwing");
                }
                finally
                {
                    SafeDeleteDirectory(repoPath);
                }
            }).ConfigureAwait(false);

            // Boundary: exactly one commit ahead (B..C), distinct from the multi-commit case.
            await RunTest("GetCommitCountBetweenAsync_BtoC_Returns1", async () =>
            {
                (string repoPath, string _, string commitB, string commitC) = await CreateRepoWithThreeCommitsAsync().ConfigureAwait(false);
                try
                {
                    int count = await service.GetCommitCountBetweenAsync(repoPath, commitB, commitC).ConfigureAwait(false);
                    AssertEqual(1, count, "B..C should be exactly 1 commit ahead");
                }
                finally
                {
                    SafeDeleteDirectory(repoPath);
                }
            }).ConfigureAwait(false);

            // Direction matters: when toRef is BEHIND fromRef the ahead-count is 0. This is the
            // core "behind-by drift" semantic -- C..A must not report A's commits as ahead.
            await RunTest("GetCommitCountBetweenAsync_ReverseDirection_Returns0", async () =>
            {
                (string repoPath, string commitA, string _, string commitC) = await CreateRepoWithThreeCommitsAsync().ConfigureAwait(false);
                try
                {
                    int count = await service.GetCommitCountBetweenAsync(repoPath, commitC, commitA).ConfigureAwait(false);
                    AssertEqual(0, count, "C..A (toRef behind fromRef) should be 0 commits ahead");
                }
                finally
                {
                    SafeDeleteDirectory(repoPath);
                }
            }).ConfigureAwait(false);

            // Error path distinct from invalid-ref-in-valid-repo: a real directory that is not a
            // git repository at all. git rev-list fails; the method must swallow and return 0.
            await RunTest("GetCommitCountBetweenAsync_NonRepoPath_Returns0WithoutThrowing", async () =>
            {
                string nonRepoPath = Path.Combine(Path.GetTempPath(), "armada_gitcount_nonrepo_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(nonRepoPath);
                try
                {
                    int count = await service.GetCommitCountBetweenAsync(nonRepoPath, "HEAD~1", "HEAD").ConfigureAwait(false);
                    AssertEqual(0, count, "A non-git directory should return 0 without throwing");
                }
                finally
                {
                    SafeDeleteDirectory(nonRepoPath);
                }
            }).ConfigureAwait(false);
        }
    }
}

