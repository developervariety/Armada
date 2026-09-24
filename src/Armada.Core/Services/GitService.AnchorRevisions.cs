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

        /// <inheritdoc />
        public async Task<string?> ReadFileExcerptOnRevisionAsync(string worktreePath,
            string revision, string relativePath, int line, int maxLines, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(worktreePath) || String.IsNullOrWhiteSpace(revision) || String.IsNullOrWhiteSpace(relativePath)) return null;
            if (maxLines < 1) return null;
            token.ThrowIfCancellationRequested();

            string content;
            try
            {
                string commit = await RequireAnchorRevisionAsync(worktreePath, revision, token).ConfigureAwait(false);
                // The show output is capped by the anchor reader, so a very large blob fails rather than
                // loading unbounded content.
                content = await RunAnchorGitAsync(worktreePath, token, "show", commit + ":" + relativePath).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }

            string[] lines = content.Replace("\r\n", "\n").Split('\n');
            int total = lines.Length;
            if (total > 0 && lines[total - 1].Length == 0) total--;
            if (total == 0) return String.Empty;

            int target = Math.Clamp(line, 1, total);
            int first = Math.Max(1, target - (maxLines / 4));
            int last = Math.Min(total, first + maxLines - 1);
            first = Math.Max(1, last - maxLines + 1);

            string window = String.Join("\n", lines, first - 1, last - first + 1);
            return RuntimeLogFormatter.RedactSecrets(window);
        }

        private static async Task<string> RunAnchorGitAsync(string path, CancellationToken token, params string[] arguments)
        {
            token.ThrowIfCancellationRequested();

            // A pinned query reads one file or listing; past 1 MiB it is refused rather than returned partial.
            BoundedProcessRequest request = new BoundedProcessRequest(GitProcessStartInfo.Create(path, arguments), GitProcessTimeouts.Resolve())
            {
                OutputLimitBytes = _AnchorOutputLimitBytes,
                OutputShape = Armada.Core.Enums.BoundedOutputShapeEnum.Head
            };
            BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
            if (result.Cancelled)
            {
                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(token);
            }
            if (result.TimedOut) throw new TimeoutException("Pinned Git query timed out.");
            if (result.Truncated) throw new InvalidOperationException("Pinned Git query output limit exceeded.");
            if (result.ExitCode != 0)
                throw new GitCommandException(result.ExitCode ?? -1, "Pinned Git query failed.");
            return result.StandardOutput;
        }

        private const int _AnchorOutputLimitBytes = 1048576;

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
