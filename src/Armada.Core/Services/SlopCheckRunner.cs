namespace Armada.Core.Services
{
    using System;
    using System.ComponentModel;
    using System.Diagnostics;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Runs the Slop check: reads the reviewed diff from the vessel repository and classifies it.
    /// </summary>
    /// <remarks>
    /// The reviewed diff is the merge base of the vessel's default branch and the reviewed commit,
    /// compared with that commit - the same change a merge would land. Every step that cannot
    /// produce that diff returns a failure that names the step. None of them yields a pass, because
    /// a check that examined nothing and reports green is worse than a red one.
    /// </remarks>
    public sealed class SlopCheckRunner
    {
        #region Public-Members

        /// <summary>
        /// The command text a Slop check record carries once it has been executed.
        /// </summary>
        public const string CommandLabel = "armada slop-classifier (native, reviewed diff)";

        #endregion

        #region Private-Members

        private const string _Header = "[SlopCheckRunner] ";
        private static readonly TimeSpan _GitTimeout = TimeSpan.FromMinutes(5);
        private readonly LoggingModule? _Logging;
        private readonly Func<IReadOnlyList<BannedDiffPatternRule>>? _BannedDiffPatterns;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Optional logging module.</param>
        /// <param name="bannedDiffPatterns">Optional accessor for the operator-configured banned-diff
        /// patterns, read on each run so a settings change takes effect without a restart. Null or an
        /// empty list means no banned-diff guard.</param>
        public SlopCheckRunner(LoggingModule? logging = null, Func<IReadOnlyList<BannedDiffPatternRule>>? bannedDiffPatterns = null)
        {
            _Logging = logging;
            _BannedDiffPatterns = bannedDiffPatterns;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Classify the reviewed diff of a commit or branch.
        /// </summary>
        /// <param name="repoPath">A git repository (bare or not) that holds the reviewed commit.</param>
        /// <param name="commitHash">The reviewed commit, when known. Wins over the branch.</param>
        /// <param name="branchName">The reviewed branch, when the commit is not known.</param>
        /// <param name="defaultBranch">The branch the work would land on.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The outcome. Never null.</returns>
        public async Task<SlopCheckOutcome> RunAsync(
            string? repoPath,
            string? commitHash,
            string? branchName,
            string? defaultBranch,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(commitHash) && String.IsNullOrWhiteSpace(branchName))
                return SlopCheckOutcome.Failure("The Slop check has no commit or branch to classify, so the reviewed diff is unknown. Nothing was examined.");

            if (String.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
                return SlopCheckOutcome.Failure("The Slop check has no repository to read the reviewed diff from (path " + (repoPath ?? "(none)") + " does not exist). Nothing was examined.");

            if (String.IsNullOrWhiteSpace(defaultBranch))
                return SlopCheckOutcome.Failure("The Slop check needs the vessel's default branch to compute the review base. Nothing was examined.");

            GitResult gitDir = await RunGitAsync(repoPath, token, "rev-parse", "--git-dir").ConfigureAwait(false);
            if (gitDir.ExitCode != 0)
                return SlopCheckOutcome.Failure("The Slop check could not use " + repoPath + " as a git repository: " + gitDir.Describe() + " Nothing was examined.");

            string? head = await ResolveReviewedCommitAsync(repoPath, commitHash, branchName, token).ConfigureAwait(false);
            if (head == null)
            {
                GitResult fetch = await RunGitAsync(repoPath, token, "fetch", "--prune", "origin").ConfigureAwait(false);
                if (fetch.ExitCode != 0)
                    _Logging?.Warn(_Header + "fetch before resolving the reviewed commit failed in " + repoPath + ": " + fetch.Describe());
                head = await ResolveReviewedCommitAsync(repoPath, commitHash, branchName, token).ConfigureAwait(false);
            }

            if (head == null)
            {
                return SlopCheckOutcome.Failure("The Slop check could not resolve the reviewed commit (commit "
                    + (commitHash ?? "(none)") + ", branch " + (branchName ?? "(none)") + ") in " + repoPath + ". Nothing was examined.");
            }

            string? baseBranch = await ResolveBranchAsync(repoPath, defaultBranch, token).ConfigureAwait(false);
            if (baseBranch == null)
                return SlopCheckOutcome.Failure("The Slop check could not resolve the default branch " + defaultBranch + " in " + repoPath + ". Nothing was examined.");

            GitResult mergeBase = await RunGitAsync(repoPath, token, "merge-base", baseBranch, head).ConfigureAwait(false);
            string reviewBase = mergeBase.StdOut.Trim();
            if (mergeBase.ExitCode != 0 || reviewBase.Length == 0)
                return SlopCheckOutcome.Failure("The Slop check found no merge base between " + defaultBranch + " and " + head + ": " + mergeBase.Describe() + " Nothing was examined.");

            GitResult diff = await RunGitAsync(repoPath, token,
                "-c", "core.quotepath=false", "diff", "--no-color", "--no-ext-diff", "-U3", reviewBase, head).ConfigureAwait(false);
            if (diff.ExitCode != 0)
                return SlopCheckOutcome.Failure("The Slop check could not read the reviewed diff " + Abbreviate(reviewBase) + ".." + Abbreviate(head) + ": " + diff.Describe() + " Nothing was examined.");

            // The operator-configured banned-diff guard runs before the slop reading and is not
            // suppressible: a change whose added lines match a configured banned pattern fails the
            // check. The pattern list is empty by default, so this is a no-op until a deployment
            // configures one; the product carries the mechanism and never a deployment's domain.
            IReadOnlyList<BannedDiffPatternRule>? bannedRules = _BannedDiffPatterns?.Invoke();
            if (bannedRules != null && bannedRules.Count > 0)
            {
                BannedDiffPatternResult banned = BannedDiffPatternClassifier.Classify(diff.StdOut, bannedRules);
                if (banned.HasBanned)
                {
                    StringBuilder bannedReport = new StringBuilder();
                    bannedReport.AppendLine("Banned-diff guard FAILED: the reviewed diff " + Abbreviate(reviewBase) + ".." + Abbreviate(head)
                        + " adds a line matching a configured banned pattern.");
                    bannedReport.AppendLine();
                    bannedReport.Append(BannedDiffPatternClassifier.FormatFindings(banned));
                    return SlopCheckOutcome.Failure(bannedReport.ToString());
                }
            }

            GitResult packages = await RunGitAsync(repoPath, token, "show", head + ":Directory.Packages.props").ConfigureAwait(false);
            bool centralPackageManagement = packages.ExitCode == 0 && SlopDiffClassifier.IsCentralPackageManagementEnabled(packages.StdOut);

            SlopClassificationResult classification = SlopDiffClassifier.Classify(diff.StdOut, centralPackageManagement);
            bool emptyDiff = String.IsNullOrWhiteSpace(diff.StdOut);

            StringBuilder report = new StringBuilder();
            report.AppendLine("Slop check: reviewed diff " + Abbreviate(reviewBase) + ".." + Abbreviate(head) + " (merge base with " + defaultBranch + ")");
            if (emptyDiff)
                report.AppendLine("The reviewed diff is empty: " + Abbreviate(head) + " is already contained in " + defaultBranch + ". No lines were classified.");
            report.AppendLine("C# and MSBuild files with added lines: " + classification.FilesExamined + "; added lines examined: " + classification.AddedLinesExamined);
            report.AppendLine("Central package management at the reviewed commit: " + (centralPackageManagement ? "enabled" : "not enabled"));
            report.AppendLine("Result: " + (classification.Passed ? "PASSED" : "FAILED"));
            report.AppendLine();
            report.Append(SlopDiffClassifier.FormatFindings(classification));

            return new SlopCheckOutcome
            {
                Completed = true,
                BaseCommit = reviewBase,
                HeadCommit = head,
                Classification = classification,
                Report = report.ToString(),
                Summary = BuildSummary(classification, emptyDiff)
            };
        }

        #endregion

        #region Private-Methods

        private static string BuildSummary(SlopClassificationResult classification, bool emptyDiff)
        {
            if (!classification.Passed)
            {
                return "Slop failed: " + classification.FailCount + " FAIL and " + classification.WarnCount
                    + " WARN finding(s), " + classification.SuppressedCount + " suppressed.";
            }

            if (classification.WarnCount > 0)
            {
                return "Slop passed with " + classification.WarnCount + " WARN finding(s), "
                    + classification.SuppressedCount + " suppressed; read the check output.";
            }

            if (emptyDiff) return "Slop passed: the reviewed diff is empty, so nothing was classified.";

            return "Slop passed: no findings in " + classification.FilesExamined + " file(s), "
                + classification.SuppressedCount + " suppressed.";
        }

        private async Task<string?> ResolveReviewedCommitAsync(string repoPath, string? commitHash, string? branchName, CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(commitHash))
            {
                string? sha = await ResolveRefAsync(repoPath, commitHash.Trim(), token).ConfigureAwait(false);
                if (sha != null) return sha;
            }

            if (!String.IsNullOrWhiteSpace(branchName))
                return await ResolveBranchAsync(repoPath, branchName.Trim(), token).ConfigureAwait(false);

            return null;
        }

        private async Task<string?> ResolveBranchAsync(string repoPath, string branchName, CancellationToken token)
        {
            foreach (string candidate in new[] { "refs/heads/" + branchName, "refs/remotes/origin/" + branchName, branchName })
            {
                string? sha = await ResolveRefAsync(repoPath, candidate, token).ConfigureAwait(false);
                if (sha != null) return sha;
            }

            return null;
        }

        private async Task<string?> ResolveRefAsync(string repoPath, string reference, CancellationToken token)
        {
            GitResult result = await RunGitAsync(repoPath, token, "rev-parse", "--verify", "--quiet", reference + "^{commit}").ConfigureAwait(false);
            if (result.ExitCode != 0) return null;
            string sha = result.StdOut.Trim();
            return sha.Length == 40 ? sha : null;
        }

        private async Task<GitResult> RunGitAsync(string workingDirectory, CancellationToken token, params string[] args)
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

            foreach (string arg in args) startInfo.ArgumentList.Add(arg);

            using (Process process = new Process { StartInfo = startInfo })
            {
                try
                {
                    if (!process.Start())
                        return new GitResult { ExitCode = -1, StdErr = "git did not start." };
                }
                catch (Win32Exception ex)
                {
                    return new GitResult { ExitCode = -1, StdErr = "git could not be started: " + ex.Message };
                }

                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();

                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(_GitTimeout);
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        KillQuietly(process);
                        if (token.IsCancellationRequested) throw;
                        return new GitResult { ExitCode = -1, StdErr = "git " + String.Join(" ", args) + " timed out after " + _GitTimeout.TotalMinutes + " minutes." };
                    }
                }

                return new GitResult
                {
                    ExitCode = process.ExitCode,
                    StdOut = await stdout.ConfigureAwait(false),
                    StdErr = await stderr.ConfigureAwait(false)
                };
            }
        }

        private void KillQuietly(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(true);
            }
            catch (InvalidOperationException ex)
            {
                _Logging?.Debug(_Header + "git process had already exited when the timeout fired: " + ex.Message);
            }
            catch (Win32Exception ex)
            {
                _Logging?.Warn(_Header + "could not kill a timed-out git process: " + ex.Message);
            }
        }

        private static string Abbreviate(string? commit)
        {
            if (String.IsNullOrWhiteSpace(commit)) return "(none)";
            string trimmed = commit.Trim();
            return trimmed.Length <= 12 ? trimmed : trimmed.Substring(0, 12);
        }

        #endregion

        #region Private-Types

        private sealed class GitResult
        {
            public int ExitCode { get; set; } = -1;
            public string StdOut { get; set; } = String.Empty;
            public string StdErr { get; set; } = String.Empty;

            public string Describe()
            {
                string message = StdErr.Trim();
                return "exit " + ExitCode + (message.Length == 0 ? "." : " (" + message + ").");
            }
        }

        #endregion
    }
}
