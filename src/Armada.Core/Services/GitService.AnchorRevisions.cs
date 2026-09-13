namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public partial class GitService
    {
        /// <inheritdoc />
        public async Task<string?> ResolveAnchorPathOnRevisionAsync(string worktreePath, string revision,
            string relativePath, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("A path is required.", nameof(relativePath));
            token.ThrowIfCancellationRequested();
            string commit = await RequireAnchorRevisionAsync(worktreePath, revision, token).ConfigureAwait(false);
            string output = await RunAnchorGitAsync(worktreePath, token, "ls-tree", "-r", "-z", "--name-only", commit).ConfigureAwait(false);
            string? suffix = null;
            int matches = 0;
            foreach (string candidate in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (candidate == relativePath) return candidate;
                if (candidate.EndsWith("/" + relativePath, StringComparison.Ordinal)) { suffix = candidate; matches++; }
            }
            if (matches > 1) throw new InvalidOperationException("Pinned path suffix is ambiguous.");
            return matches == 1 ? suffix : null;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<GitAnchorCommit>> GetCommitsTouchingPathOnRevisionAsync(string worktreePath,
            string revision, string relativePath, int maxCount, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(relativePath)) throw new ArgumentNullException(nameof(relativePath));
            if (maxCount < 1) throw new ArgumentOutOfRangeException(nameof(maxCount));
            token.ThrowIfCancellationRequested();
            string commit = await RequireAnchorRevisionAsync(worktreePath, revision, token).ConfigureAwait(false);
            string output = await RunAnchorGitAsync(worktreePath, token, "--literal-pathspecs", "log", "--no-show-signature", "--no-decorate",
                "-n", Math.Min(maxCount, 5).ToString(CultureInfo.InvariantCulture),
                "--format=%H%x00%s%x00%ad%x00", "--date=short", commit, "--", relativePath).ConfigureAwait(false);
            List<GitAnchorCommit> result = new List<GitAnchorCommit>();
            string[] fields = output.Split('\0');
            for (int index = 0; index + 2 < fields.Length; index += 3)
            {
                string sha = fields[index].TrimStart('\r', '\n');
                if (sha.Length != commit.Length) throw new InvalidOperationException("Invalid pinned history output.");
                result.Add(new GitAnchorCommit
                {
                    Sha = sha,
                    Subject = RuntimeLogFormatter.RedactSecrets(fields[index + 1]),
                    DateUtc = fields[index + 2]
                });
            }
            return result;
        }

        /// <inheritdoc />
        public async Task<GitAnchorPriorArt> SearchTrackedContentOnRevisionAsync(string worktreePath,
            string revision, string term, int maxSamples, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(term)) throw new ArgumentException("A search term is required.", nameof(term));
            if (maxSamples < 0) throw new ArgumentOutOfRangeException(nameof(maxSamples));
            token.ThrowIfCancellationRequested();
            string commit = await RequireAnchorRevisionAsync(worktreePath, revision, token).ConfigureAwait(false);
            GitAnchorPriorArt result = new GitAnchorPriorArt { Term = term };
            string output;
            try
            {
                output = await RunAnchorGitAsync(worktreePath, token, "grep", "-z", "--files-with-matches", "-I",
                    "--fixed-strings", "-e", term, commit, "--").ConfigureAwait(false);
            }
            catch (GitCommandException exception) when (exception.ExitCode == 1)
            {
                return result;
            }
            foreach (string file in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                StripAnchorRevision(file, commit);
                result.MatchingFileCount++;
            }
            result.Found = result.MatchingFileCount > 0;
            if (!result.Found || maxSamples == 0) return result;
            // Both passes address the same immutable commit. Any second-pass failure is an error,
            // not a verified empty sample set.
            output = await RunAnchorGitAsync(worktreePath, token, "grep", "-z", "--line-number", "-I",
                "--fixed-strings", "--max-count=1", "-e", term, commit, "--").ConfigureAwait(false);
            int offset = 0;
            while (offset < output.Length && result.SampleLocations.Count < Math.Min(maxSamples, 3))
            {
                int pathEnd = output.IndexOf('\0', offset);
                int lineEnd = pathEnd < 0 ? -1 : output.IndexOf('\0', pathEnd + 1);
                if (pathEnd < 0 || lineEnd < 0) throw new InvalidOperationException("Invalid pinned search output.");
                string path = StripAnchorRevision(output.Substring(offset, pathEnd - offset), commit);
                string line = output.Substring(pathEnd + 1, lineEnd - pathEnd - 1);
                if (!Int32.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < 1)
                    throw new InvalidOperationException("Invalid pinned search location.");
                if (RuntimeLogFormatter.RedactSecrets(path) != path)
                    throw new InvalidOperationException("Pinned search path contains protected data.");
                result.SampleLocations.Add(path + ":" + line);
                int next = output.IndexOf('\n', lineEnd + 1);
                offset = next < 0 ? output.Length : next + 1;
            }
            return result;
        }

        private static async Task<string> RunAnchorGitAsync(string path, CancellationToken token, params string[] arguments)
        {
            System.Diagnostics.ProcessStartInfo start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git", WorkingDirectory = path, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["GCM_INTERACTIVE"] = "Never";
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using (CancellationTokenSource timeout = new CancellationTokenSource(GitProcessTimeouts.Resolve()))
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token))
            using (System.Diagnostics.Process process = new System.Diagnostics.Process { StartInfo = start })
            {
                token.ThrowIfCancellationRequested();
                process.Start();
                try
                {
                    Task<string> stdout = ReadAnchorOutputAsync(process.StandardOutput, process, linked.Token);
                    Task<string> stderr = ReadAnchorOutputAsync(process.StandardError, process, linked.Token);
                    await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                    await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                    if (process.ExitCode != 0)
                        throw new GitCommandException(process.ExitCode, "Pinned Git query failed.");
                    return await stdout.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException("Pinned Git query timed out.");
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    }
                }
            }
        }

        private static async Task<string> ReadAnchorOutputAsync(System.IO.StreamReader reader,
            System.Diagnostics.Process process, CancellationToken token)
        {
            const int maximumCharacters = 1048576;
            System.Text.StringBuilder output = new System.Text.StringBuilder();
            char[] buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > maximumCharacters)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    throw new InvalidOperationException("Pinned Git query output limit exceeded.");
                }
                output.Append(buffer, 0, count);
            }
            return output.ToString();
        }

        private async Task<string> RequireAnchorRevisionAsync(string path, string revision, CancellationToken token)
        {
            string? commit = await GetRevisionCommitShaAsync(path, revision, token).ConfigureAwait(false);
            if (String.IsNullOrEmpty(commit)) throw new InvalidOperationException("Pinned anchor revision is unavailable.");
            return commit;
        }

        private static string StripAnchorRevision(string path, string commit)
        {
            string prefix = commit + ":";
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length == prefix.Length)
                throw new InvalidOperationException("Invalid pinned search path.");
            return path.Substring(prefix.Length);
        }
    }
}
