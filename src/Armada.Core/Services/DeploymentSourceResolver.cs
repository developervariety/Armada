namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Resolves a deployment source ref (branch, tag, abbreviated or full commit) to one full commit in the vessel's
    /// own repository. The vessel's local repository is read first, because it holds the landed branches; the working
    /// directory is read when the local repository is absent. Discovery never climbs above the named directory, so a
    /// directory that merely sits inside another checkout is not mistaken for the vessel's repository.
    /// </summary>
    public class DeploymentSourceResolver
    {
        #region Private-Members

        private static readonly Regex _FullCommit = new Regex("^[0-9a-fA-F]{40}$", RegexOptions.Compiled);
        private readonly TimeSpan _GitTimeout;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="gitTimeout">Longest time one git command may run; defaults to one minute.</param>
        public DeploymentSourceResolver(TimeSpan? gitTimeout = null)
        {
            TimeSpan timeout = gitTimeout ?? TimeSpan.FromMinutes(1);
            _GitTimeout = timeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : timeout;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Resolve a source ref for a vessel.
        /// </summary>
        /// <param name="vessel">Vessel whose repository holds the ref.</param>
        /// <param name="sourceRef">Branch, tag, or commit.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The resolution; never null.</returns>
        public async Task<DeploymentSourceResolution> ResolveAsync(Vessel vessel, string sourceRef, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (String.IsNullOrWhiteSpace(sourceRef)) throw new ArgumentNullException(nameof(sourceRef));

            string reference = sourceRef.Trim();
            DeploymentSourceResolution result = new DeploymentSourceResolution();
            List<string> failures = new List<string>();

            foreach (string path in CandidatePaths(vessel))
            {
                RepositoryProbe probe = await ProbeRepositoryAsync(path, token).ConfigureAwait(false);
                if (!probe.IsRepository)
                {
                    if (!String.IsNullOrEmpty(probe.Error)) failures.Add(path + ": " + probe.Error);
                    continue;
                }

                result.RepositoryFound = true;
                result.RepositoryPath ??= path;
                foreach (string candidate in RefCandidates(reference))
                {
                    string? commit = await RevParseCommitAsync(path, candidate, token).ConfigureAwait(false);
                    if (commit != null)
                    {
                        result.Commit = commit.ToLowerInvariant();
                        result.RepositoryPath = path;
                        result.Reason = String.Empty;
                        return result;
                    }
                }

                failures.Add(path + ": ref " + reference + " names no commit");
            }

            if (result.RepositoryFound || failures.Count > 0)
            {
                // A repository that refused to answer is treated like one that answered "no": the deployment must
                // not fall back to an unpinned source because git could not be read.
                result.RepositoryFound = true;
                result.Reason = "deployment_source_unresolved: source ref " + reference + " does not resolve to a commit in vessel "
                    + vessel.Name + " (" + String.Join("; ", failures) + ").";
            }
            else
            {
                result.Reason = "vessel " + vessel.Name + " has no git repository to resolve source ref " + reference + " in.";
            }

            return result;
        }

        #endregion

        #region Private-Methods

        private static List<string> CandidatePaths(Vessel vessel)
        {
            List<string> paths = new List<string>();
            AddPath(paths, vessel.LocalPath);
            AddPath(paths, vessel.WorkingDirectory);
            return paths;
        }

        private static void AddPath(List<string> paths, string? path)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            string full;
            try
            {
                full = Path.GetFullPath(path.Trim());
            }
            catch (Exception)
            {
                return;
            }

            if (!Directory.Exists(full)) return;
            foreach (string existing in paths)
            {
                if (String.Equals(existing, full, StringComparison.Ordinal)) return;
            }
            paths.Add(full);
        }

        private static List<string> RefCandidates(string reference)
        {
            List<string> candidates = new List<string>();
            if (_FullCommit.IsMatch(reference))
            {
                candidates.Add(reference);
                return candidates;
            }

            if (!reference.StartsWith("refs/", StringComparison.Ordinal))
            {
                candidates.Add("refs/heads/" + reference);
                candidates.Add("refs/remotes/origin/" + reference);
                candidates.Add("refs/tags/" + reference);
            }
            candidates.Add(reference);
            return candidates;
        }

        private async Task<RepositoryProbe> ProbeRepositoryAsync(string path, CancellationToken token)
        {
            BoundedProcessResult result;
            try
            {
                result = await RunGitAsync(path, token, "rev-parse", "--git-dir").ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new RepositoryProbe { IsRepository = false, Error = "git could not run: " + ex.Message };
            }

            if (result.Succeeded) return new RepositoryProbe { IsRepository = true };

            string error = (result.StandardError ?? String.Empty).Trim();
            if (error.IndexOf("not a git repository", StringComparison.OrdinalIgnoreCase) >= 0)
                return new RepositoryProbe { IsRepository = false };

            return new RepositoryProbe
            {
                IsRepository = false,
                Error = result.TimedOut
                    ? "git timed out"
                    : (String.IsNullOrEmpty(error) ? "git exited " + (result.ExitCode?.ToString() ?? "without a code") : error)
            };
        }

        private async Task<string?> RevParseCommitAsync(string path, string reference, CancellationToken token)
        {
            BoundedProcessResult result = await RunGitAsync(path, token, "rev-parse", "--verify", "--quiet", reference + "^{commit}").ConfigureAwait(false);
            if (!result.Succeeded) return null;
            string sha = (result.StandardOutput ?? String.Empty).Trim();
            return _FullCommit.IsMatch(sha) ? sha : null;
        }

        private async Task<BoundedProcessResult> RunGitAsync(string path, CancellationToken token, params string[] args)
        {
            ProcessStartInfo startInfo = GitProcessStartInfo.Create(path, args);
            startInfo.Environment["LC_ALL"] = "C";
            string? parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!String.IsNullOrEmpty(parent)) startInfo.Environment["GIT_CEILING_DIRECTORIES"] = parent;

            BoundedProcessRequest request = new BoundedProcessRequest(startInfo, _GitTimeout)
            {
                OutputLimitBytes = 64 * 1024
            };
            BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
            if (result.Cancelled)
            {
                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(token);
            }
            return result;
        }

        #endregion

        #region Private-Types

        private sealed class RepositoryProbe
        {
            public bool IsRepository { get; set; } = false;

            public string? Error { get; set; } = null;
        }

        #endregion
    }
}
