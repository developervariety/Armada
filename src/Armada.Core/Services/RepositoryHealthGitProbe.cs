namespace Armada.Core.Services
{
    using System;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Reads repository health with a fixed set of Git commands. The commands are
    /// <c>rev-parse</c>, <c>status --porcelain</c>, <c>ls-remote</c>, and <c>rev-list --count</c>.
    /// None of them fetch, and none of them update a ref or the worktree. <c>--no-optional-locks</c>
    /// keeps status from writing the index.
    /// </summary>
    public static class RepositoryHealthGitProbe
    {
        private static readonly TimeSpan _Timeout = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Read one bare repository and one working checkout. A missing path is recorded as absent.
        /// </summary>
        /// <param name="barePath">Bare repository path, or null.</param>
        /// <param name="workingPath">Working checkout path, or null.</param>
        /// <param name="defaultBranch">Vessel default branch.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The facts.</returns>
        public static async Task<RepositoryHealthGitFacts> ReadAsync(string? barePath, string? workingPath, string defaultBranch, CancellationToken token)
        {
            if (!SafeBranch(defaultBranch)) throw new ArgumentException("The default branch is not a safe ref name.", nameof(defaultBranch));
            RepositoryHealthGitFacts facts = new RepositoryHealthGitFacts();
            bool bareIsRepo = await IsGitDirectoryAsync(barePath, token).ConfigureAwait(false);
            bool bareIsBare = bareIsRepo && await IsBareAsync(barePath!, token).ConfigureAwait(false);
            bool workingIsRepo = await IsGitDirectoryAsync(workingPath, token).ConfigureAwait(false);
            bool workingIsBare = workingIsRepo && await IsBareAsync(workingPath!, token).ConfigureAwait(false);

            facts.RepositoryReachable = bareIsRepo || workingIsRepo;
            bool branchOk = true;
            if (bareIsBare)
            {
                facts.BarePresent = true;
                string? local = await VerifyCommitAsync(barePath!, "refs/heads/" + defaultBranch, token).ConfigureAwait(false);
                if (local == null) branchOk = false;
                else await CompareOriginAsync(barePath!, defaultBranch, local, facts, bare: true, token).ConfigureAwait(false);
            }
            else if (bareIsRepo)
            {
                branchOk = await VerifyCommitAsync(barePath!, "refs/heads/" + defaultBranch, token).ConfigureAwait(false) != null && branchOk;
            }

            if (workingIsRepo && !workingIsBare)
            {
                facts.WorkingCheckoutPresent = true;
                string? local = await VerifyCommitAsync(workingPath!, "HEAD", token).ConfigureAwait(false);
                string? branch = await VerifyCommitAsync(workingPath!, "refs/heads/" + defaultBranch, token).ConfigureAwait(false);
                if (branch == null) branchOk = false;
                facts.WorkingCheckoutClean = await IsCleanAsync(workingPath!, token).ConfigureAwait(false);
                if (local != null) await CompareOriginAsync(workingPath!, defaultBranch, local, facts, bare: false, token).ConfigureAwait(false);
            }

            facts.DefaultBranchResolvable = facts.RepositoryReachable && branchOk;
            return facts;
        }

        private static async Task CompareOriginAsync(string path, string branch, string localSha, RepositoryHealthGitFacts facts, bool bare, CancellationToken token)
        {
            string? remote = await RemoteTipAsync(path, branch, token).ConfigureAwait(false);
            if (remote == null)
            {
                SetCounts(facts, bare, null, null, false);
                return;
            }
            if (String.Equals(remote, localSha, StringComparison.OrdinalIgnoreCase))
            {
                SetCounts(facts, bare, 0, 0, false);
                return;
            }

            int? ahead = await CountAsync(path, remote, localSha, token).ConfigureAwait(false);
            int? behind = await CountAsync(path, localSha, remote, token).ConfigureAwait(false);
            bool incomplete = ahead == null || behind == null;
            SetCounts(facts, bare, ahead, behind, incomplete);
        }

        private static void SetCounts(RepositoryHealthGitFacts facts, bool bare, int? ahead, int? behind, bool incomplete)
        {
            if (bare)
            {
                facts.BareAhead = ahead;
                facts.BareBehind = behind;
                facts.BareComparisonIncomplete = incomplete;
            }
            else
            {
                facts.WorkingAhead = ahead;
                facts.WorkingBehind = behind;
                facts.WorkingComparisonIncomplete = incomplete;
            }
        }

        private static async Task<bool> IsGitDirectoryAsync(string? path, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
            GitOutput output = await RunAsync(path, token, "rev-parse", "--is-inside-work-tree").ConfigureAwait(false);
            if (output.ExitCode == 0) return true;
            GitOutput gitDir = await RunAsync(path, token, "rev-parse", "--git-dir").ConfigureAwait(false);
            return gitDir.ExitCode == 0;
        }

        private static async Task<bool> IsBareAsync(string path, CancellationToken token)
        {
            GitOutput output = await RunAsync(path, token, "rev-parse", "--is-bare-repository").ConfigureAwait(false);
            return output.ExitCode == 0 && String.Equals(output.Text.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string?> VerifyCommitAsync(string path, string revision, CancellationToken token)
        {
            GitOutput output = await RunAsync(path, token, "rev-parse", "--verify", "--end-of-options", revision + "^{commit}").ConfigureAwait(false);
            if (output.ExitCode != 0) return null;
            string sha = output.Text.Trim();
            return sha.Length >= 40 ? sha : null;
        }

        private static async Task<bool> IsCleanAsync(string path, CancellationToken token)
        {
            GitOutput output = await RunAsync(path, token, "status", "--porcelain", "--untracked-files=all").ConfigureAwait(false);
            return output.ExitCode == 0 && String.IsNullOrWhiteSpace(output.Text);
        }

        private static async Task<string?> RemoteTipAsync(string path, string branch, CancellationToken token)
        {
            GitOutput output = await RunAsync(path, token, "ls-remote", "origin", "refs/heads/" + branch).ConfigureAwait(false);
            if (output.ExitCode != 0) return null;
            foreach (string line in output.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 2) continue;
                if (!String.Equals(parts[1].Trim(), "refs/heads/" + branch, StringComparison.Ordinal)) continue;
                string sha = parts[0].Trim();
                if (sha.Length >= 40) return sha;
            }
            return null;
        }

        private static async Task<int?> CountAsync(string path, string fromSha, string toSha, CancellationToken token)
        {
            GitOutput output = await RunAsync(path, token, "rev-list", "--count", fromSha + ".." + toSha).ConfigureAwait(false);
            if (output.ExitCode != 0) return null;
            return Int32.TryParse(output.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count) ? count : null;
        }

        private static bool SafeBranch(string branch)
        {
            if (String.IsNullOrWhiteSpace(branch) || branch.Length > 200) return false;
            if (branch.Contains("..", StringComparison.Ordinal) || branch.StartsWith('-') || branch.Contains(':') || branch.Contains(' ')) return false;
            foreach (char character in branch)
            {
                if (!(Char.IsLetterOrDigit(character) || character == '/' || character == '.' || character == '_' || character == '-'))
                    return false;
            }
            return true;
        }

        private static async Task<GitOutput> RunAsync(string workingDirectory, CancellationToken token, params string[] arguments)
        {
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            start.ArgumentList.Add("--no-optional-locks");
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using Process process = new Process { StartInfo = start };
            if (!process.Start()) return new GitOutput(1, String.Empty);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(token);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new GitOutput(1, String.Empty);
            }

            string text = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return new GitOutput(process.ExitCode, text);
        }

        private readonly struct GitOutput
        {
            public GitOutput(int exitCode, string text)
            {
                ExitCode = exitCode;
                Text = text ?? String.Empty;
            }

            public int ExitCode { get; }
            public string Text { get; }
        }
    }
}
