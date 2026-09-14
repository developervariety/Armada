namespace Armada.Test.Common
{
    using System;
    using System.Diagnostics;
    using System.IO;

    /// <summary>
    /// Helper for creating local bare git repositories for use in automated tests.
    /// Avoids cloning from fake GitHub URLs which causes 500 errors when the server
    /// tries to provision docks for mission assignment.
    /// </summary>
    public static class TestRepoHelper
    {
        #region Private-Members

        private static string? _BareRepoPath = null;
        private static readonly object _Lock = new object();

        #endregion

        #region Public-Methods

        /// <summary>
        /// Returns a file:// URL to a local bare git repository with at least one commit.
        /// The repository is created once and reused across all test suites.
        /// </summary>
        /// <returns>A file:// URL suitable for use as a vessel RepoUrl.</returns>
        public static string GetLocalBareRepoUrl()
        {
            lock (_Lock)
            {
                if (_BareRepoPath == null)
                {
                    string tempBase = Path.Combine(Path.GetTempPath(), "armada_test_bare_" + Guid.NewGuid().ToString("N"));
                    string workDir = Path.Combine(tempBase, "work");
                    _BareRepoPath = Path.Combine(tempBase, "bare.git");
                    Directory.CreateDirectory(workDir);

                    RunGit(workDir, "init -b main");
                    RunGit(workDir, "config user.email test@test.com");
                    RunGit(workDir, "config user.name Test");
                    File.WriteAllText(Path.Combine(workDir, "README.md"), "test");
                    RunGit(workDir, "add .");
                    RunGit(workDir, "commit -m init");
                    RunGit(tempBase, "clone --bare " + workDir.Replace("\\", "/") + " bare.git");

                    try { Directory.Delete(workDir, true); } catch { }
                }
                return "file:///" + _BareRepoPath.Replace("\\", "/");
            }
        }

        /// <summary>
        /// Returns the real commit at the fixture repository main branch.
        /// </summary>
        /// <returns>The full commit identifier.</returns>
        public static string GetLocalBareRepoHeadCommit()
        {
            lock (_Lock)
            {
                if (_BareRepoPath == null) _ = GetLocalBareRepoUrl();
                return RunGitOutput(_BareRepoPath!, "rev-parse", "refs/heads/main");
            }
        }

        /// <summary>
        /// Creates a new bare repository whose main branch holds one commit, for a vessel whose
        /// LocalPath the server may read or delete. The shared fixture repository is never exposed
        /// as a LocalPath because deleting such a vessel removes that directory.
        /// </summary>
        /// <returns>The bare repository path and the commit at its main branch.</returns>
        public static DedicatedBareRepo CreateDedicatedBareRepo()
        {
            string tempBase = Path.Combine(Path.GetTempPath(), "armada_test_dedicated_" + Guid.NewGuid().ToString("N"));
            string workDir = Path.Combine(tempBase, "work");
            string barePath = Path.Combine(tempBase, "bare.git");
            Directory.CreateDirectory(workDir);

            RunGitChecked(workDir, "init", "-b", "main");
            RunGitChecked(workDir, "config", "user.email", "test@test.com");
            RunGitChecked(workDir, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(workDir, "README.md"), "dedicated");
            RunGitChecked(workDir, "add", ".");
            RunGitChecked(workDir, "commit", "-m", "init");
            RunGitChecked(tempBase, "clone", "--bare", workDir, barePath);
            string head = RunGitOutput(barePath, "rev-parse", "refs/heads/main");
            return new DedicatedBareRepo(barePath, head);
        }

        private static void RunGitChecked(string workingDirectory, params string[] arguments)
        {
            Process process = new Process();
            process.StartInfo.FileName = "git";
            process.StartInfo.WorkingDirectory = workingDirectory;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd().Trim();
            if (!process.WaitForExit(30000))
                throw new InvalidOperationException("git " + String.Join(" ", arguments) + " did not exit within 30 seconds.");
            if (process.ExitCode != 0)
                throw new InvalidOperationException("git " + String.Join(" ", arguments) + " failed with exit code "
                    + process.ExitCode + ": " + error);
        }

        #endregion

        #region Private-Methods

        private static void RunGit(string workingDir, string arguments)
        {
            Process process = new Process();
            process.StartInfo.FileName = "git";
            process.StartInfo.Arguments = arguments;
            process.StartInfo.WorkingDirectory = workingDir;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.Start();
            process.WaitForExit(10000);
        }

        private static string RunGitOutput(string workingDirectory, params string[] arguments)
        {
            Process process = new Process();
            process.StartInfo.FileName = "git";
            process.StartInfo.WorkingDirectory = workingDirectory;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(10000);
            if (process.ExitCode != 0 || output.Length == 0)
                throw new InvalidOperationException("Fixture repository did not expose its main commit.");
            return output;
        }

        #endregion
    }
}
