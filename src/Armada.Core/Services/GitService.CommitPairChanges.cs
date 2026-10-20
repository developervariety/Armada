namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class GitService
    {
        /// <inheritdoc />
        public async Task<ChangedPathStatusRead> ReadChangedPathStatusesBetweenCommitsAsync(string repoPath, string fromCommit, string toCommit,
            int maxPaths, TimeSpan timeout, int outputLimitBytes, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(repoPath)) throw new ArgumentNullException(nameof(repoPath));
            if (maxPaths < 1) throw new ArgumentOutOfRangeException(nameof(maxPaths));
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            token.ThrowIfCancellationRequested();

            string from = (fromCommit ?? String.Empty).Trim();
            string to = (toCommit ?? String.Empty).Trim();
            if (!DockGitAnchorPersistence.IsCommit(from) || !DockGitAnchorPersistence.IsCommit(to))
                return ChangedPathStatusRead.Unavailable(ChangedPathStatusRead.InvalidCommitCode,
                    "Both endpoints must be full commit ids; a branch name or an abbreviation is not accepted.");

            // Each endpoint is verified on its own, so a missing object is named instead of reading as a git error.
            ChangedPathStatusRead? missing = await VerifyCommitPairEndpointAsync(repoPath, from,
                ChangedPathStatusRead.StartCommitMissingCode, "The start commit is not in the repository.", timeout, token).ConfigureAwait(false);
            if (missing != null) return missing;
            missing = await VerifyCommitPairEndpointAsync(repoPath, to,
                ChangedPathStatusRead.FinalCommitMissingCode, "The final commit is not in the repository.", timeout, token).ConfigureAwait(false);
            if (missing != null) return missing;

            // diff-tree is plumbing: user diff settings, external diff drivers and pagers do not change its output.
            // Rename detection is explicit, and NUL separation keeps every path byte-exact.
            BoundedProcessResult result = await RunCommitPairGitAsync(repoPath, timeout, outputLimitBytes, token,
                "diff-tree", "-r", "-z", "-M", "--name-status", "--no-commit-id", from, to).ConfigureAwait(false);
            ChangedPathStatusRead? failure = ClassifyCommitPairProcess(result, "comparison");
            if (failure != null)
            {
                _Logging.Debug(_Header + "commit pair comparison in " + repoPath + " unavailable (" + failure.FailureCode + "): "
                    + RuntimeLogFormatter.RedactSecrets(result.StandardError));
                return failure;
            }

            List<ChangedPathEntry>? entries = ParseNameStatusOutput(result.StandardOutput);
            if (entries == null)
                return ChangedPathStatusRead.Unavailable(ChangedPathStatusRead.UnreadableOutputCode, "Git printed a status list that could not be read.");
            return ChangedPathStatusRead.FromEntries(entries, maxPaths);
        }

        /// <summary>
        /// Map a finished git process to the named failure it represents, or null when it succeeded. Cancellation is
        /// not a failure: it is rethrown so the caller's cancellation propagates.
        /// </summary>
        /// <param name="result">Process result.</param>
        /// <param name="what">What the process read, for the message.</param>
        /// <returns>The unavailable read, or null on success.</returns>
        internal static ChangedPathStatusRead? ClassifyCommitPairProcess(BoundedProcessResult result, string what)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (result.Cancelled) throw new OperationCanceledException("The " + what + " was cancelled.");
            if (result.TimedOut)
                return ChangedPathStatusRead.Unavailable(ChangedPathStatusRead.TimeoutCode, "The " + what + " passed its deadline and was stopped.");
            if (result.StandardOutputTruncated)
                return ChangedPathStatusRead.Unavailable(ChangedPathStatusRead.OutputLimitCode,
                    "The " + what + " printed more than its output limit, so no partial list is returned.");
            if (result.ExitCode != 0)
                return ChangedPathStatusRead.Unavailable(ChangedPathStatusRead.GitFailedCode,
                    "Git exited with code " + (result.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown") + " during the " + what + ".");
            return null;
        }

        /// <summary>
        /// Parse NUL-separated <c>--name-status</c> output: a status field, then one path, or two paths (previous,
        /// then current) for a rename. Returns null when any field does not fit that shape.
        /// </summary>
        /// <param name="output">Raw git output.</param>
        /// <returns>The entries in git's order, or null when the output is unreadable.</returns>
        internal static List<ChangedPathEntry>? ParseNameStatusOutput(string output)
        {
            List<ChangedPathEntry> entries = new List<ChangedPathEntry>();
            if (String.IsNullOrEmpty(output)) return entries;

            string[] fields = output.Split('\0');
            int index = 0;
            while (index < fields.Length)
            {
                string status = fields[index];
                if (status.Length == 0)
                {
                    // Only the empty field after the final separator may be empty.
                    if (index == fields.Length - 1) break;
                    return null;
                }

                char kind = status[0];
                if (kind == 'R')
                {
                    if (index + 2 >= fields.Length || fields[index + 1].Length == 0 || fields[index + 2].Length == 0) return null;
                    entries.Add(new ChangedPathEntry { Status = ChangedPathStatusEnum.Renamed, PreviousPath = fields[index + 1], Path = fields[index + 2] });
                    index += 3;
                    continue;
                }

                if (status.Length != 1 || index + 1 >= fields.Length || fields[index + 1].Length == 0) return null;
                ChangedPathStatusEnum mapped;
                switch (kind)
                {
                    case 'A': mapped = ChangedPathStatusEnum.Added; break;
                    case 'M': mapped = ChangedPathStatusEnum.Modified; break;
                    case 'D': mapped = ChangedPathStatusEnum.Deleted; break;
                    case 'T': mapped = ChangedPathStatusEnum.TypeChanged; break;
                    default: return null;
                }
                entries.Add(new ChangedPathEntry { Status = mapped, Path = fields[index + 1] });
                index += 2;
            }
            return entries;
        }

        private async Task<ChangedPathStatusRead?> VerifyCommitPairEndpointAsync(string repoPath, string commit, string missingCode,
            string missingMessage, TimeSpan timeout, CancellationToken token)
        {
            BoundedProcessResult result = await RunCommitPairGitAsync(repoPath, timeout, 64 * 1024, token,
                "rev-parse", "--verify", "--quiet", "--end-of-options", commit + "^{commit}").ConfigureAwait(false);
            // With --quiet, an object that does not exist (or is not a commit) exits 1 and prints nothing; any other
            // failure, such as a path that is not a repository, is a git error.
            if (!result.Cancelled && !result.TimedOut && result.ExitCode == 1)
                return ChangedPathStatusRead.Unavailable(missingCode, missingMessage);
            ChangedPathStatusRead? failure = ClassifyCommitPairProcess(result, "commit check");
            if (failure != null) return failure;
            if (!String.Equals(result.StandardOutput.Trim(), commit, StringComparison.OrdinalIgnoreCase))
                return ChangedPathStatusRead.Unavailable(missingCode, missingMessage);
            return null;
        }

        private static async Task<BoundedProcessResult> RunCommitPairGitAsync(string repoPath, TimeSpan timeout, int outputLimitBytes,
            CancellationToken token, params string[] arguments)
        {
            token.ThrowIfCancellationRequested();
            ProcessStartInfo startInfo = GitProcessStartInfo.Create(repoPath, arguments);
            startInfo.StandardOutputEncoding = new UTF8Encoding(false);
            startInfo.StandardErrorEncoding = new UTF8Encoding(false);
            BoundedProcessRequest request = new BoundedProcessRequest(startInfo, timeout)
            {
                OutputLimitBytes = outputLimitBytes,
                OutputShape = BoundedOutputShapeEnum.Head
            };
            BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
            if (result.Cancelled) token.ThrowIfCancellationRequested();
            return result;
        }
    }
}
