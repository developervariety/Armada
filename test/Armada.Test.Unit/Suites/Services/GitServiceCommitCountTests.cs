namespace Armada.Test.Unit.Suites.Services
{
    using System.Diagnostics;
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
            if (!IsGitOnPath())
            {
                Console.WriteLine("  SKIP  GitServiceCommitCountTests -- git not found on PATH");
                return;
            }

            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            GitService service = new GitService(logging);

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
                    Directory.Delete(repoPath, true);
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
                    Directory.Delete(repoPath, true);
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
                    Directory.Delete(repoPath, true);
                }
            }).ConfigureAwait(false);
        }
    }
}
