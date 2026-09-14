namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Guarded operator push and merge for a vessel's landing repository.
    /// </summary>
    /// <remarks>
    /// A managed vessel lands onto its landing repository (the vessel local path) and keeps a
    /// separate working checkout. Every write here names its source, target and (for a push) its
    /// remote explicitly, validates ref names with git, refuses when the working checkout is dirty
    /// or detached, holds the vessel's repository slot shared with mission landing and the merge
    /// queue, never force-updates or deletes a ref, and verifies the resulting ancestry. Work that
    /// still belongs to a mission or an active merge-queue entry is refused so manual writes cannot
    /// step around review and Check gates. Every refusal carries a reason code and changes no refs.
    /// </remarks>
    public class VesselBranchWriteService
    {
        /// <summary>The only remote a push may target.</summary>
        public const string AllowedRemote = "origin";

        /// <summary>Fast-forward merge strategy.</summary>
        public const string StrategyFastForward = "FastForward";

        /// <summary>Explicit merge-commit strategy.</summary>
        public const string StrategyMergeCommit = "MergeCommit";

        private const string _Header = "[VesselBranchWrite] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;

        private sealed class GitRun
        {
            public int ExitCode { get; set; }
            public string Stdout { get; set; } = String.Empty;
            public string Stderr { get; set; } = String.Empty;
            public bool Ok => ExitCode == 0;
        }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver used for mission and merge-queue gates.</param>
        /// <param name="logging">Logging module.</param>
        public VesselBranchWriteService(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <summary>
        /// Describe which write controls the caller may request for this vessel.
        /// </summary>
        /// <param name="vessel">Vessel.</param>
        /// <param name="isAdministrator">Whether the caller administers the vessel's tenant.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Control availability with reason codes.</returns>
        public async Task<BranchWriteControls> DescribeControlsAsync(Vessel vessel, bool isAdministrator, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            BranchWriteControls controls = new BranchWriteControls { Remote = AllowedRemote };
            if (!isAdministrator)
            {
                controls.MergeUnavailableReason = BranchWriteReasons.AdministratorRequired;
                controls.PushUnavailableReason = BranchWriteReasons.AdministratorRequired;
                return controls;
            }

            string? repository = await ResolveRepositoryAsync(vessel, token).ConfigureAwait(false);
            if (repository == null)
            {
                controls.MergeUnavailableReason = BranchWriteReasons.RepositoryMissing;
                controls.PushUnavailableReason = BranchWriteReasons.RepositoryMissing;
                return controls;
            }

            controls.MergeAvailable = true;
            BranchWriteResult? remoteRefusal = await VerifyOriginAsync(repository, vessel, new BranchWriteResult(), token).ConfigureAwait(false);
            if (remoteRefusal == null) controls.PushAvailable = true;
            else controls.PushUnavailableReason = remoteRefusal.Reason;
            return controls;
        }

        /// <summary>
        /// Merge one branch into another inside the vessel landing repository. Nothing is pushed.
        /// </summary>
        /// <param name="vessel">Vessel.</param>
        /// <param name="request">Explicit merge request.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Verified result or a named refusal.</returns>
        public async Task<BranchWriteResult> MergeAsync(Vessel vessel, BranchMergeRequest? request, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            BranchWriteResult result = new BranchWriteResult
            {
                Operation = "merge",
                VesselId = vessel.Id,
                SourceRef = request?.SourceRef,
                TargetRef = request?.TargetRef,
                Strategy = request?.Strategy
            };
            if (request == null || String.IsNullOrWhiteSpace(request.SourceRef) || String.IsNullOrWhiteSpace(request.TargetRef))
                return Refuse(result, BranchWriteReasons.InvalidRequest, "SourceRef and TargetRef are required.");

            string? strategy = null;
            if (String.Equals(request.Strategy, StrategyFastForward, StringComparison.OrdinalIgnoreCase)) strategy = StrategyFastForward;
            else if (String.Equals(request.Strategy, StrategyMergeCommit, StringComparison.OrdinalIgnoreCase)) strategy = StrategyMergeCommit;
            if (strategy == null)
                return Refuse(result, BranchWriteReasons.InvalidStrategy, "Strategy must be FastForward or MergeCommit.");
            result.Strategy = strategy;

            string source = request.SourceRef;
            string target = request.TargetRef;
            BranchWriteResult? refusal = await ValidateCommonAsync(vessel, source, target, requireMissionGate: true, result, token).ConfigureAwait(false);
            if (refusal != null) return refusal;
            string repository = (await ResolveRepositoryAsync(vessel, token).ConfigureAwait(false))!;

            using IDisposable? lease = VesselRepositoryLock.TryAcquire(vessel.Id);
            if (lease == null)
                return Refuse(result, BranchWriteReasons.VesselBusy, "Another landing or branch write is in progress for this vessel. Retry when it finishes.");

            refusal = await ValidateWorkingCheckoutAsync(vessel, result, token).ConfigureAwait(false);
            if (refusal != null) return refusal;

            string? sourceCommit = await ResolveBranchCommitAsync(repository, source, token).ConfigureAwait(false);
            if (sourceCommit == null) return Refuse(result, BranchWriteReasons.SourceMissing, "Source branch '" + source + "' does not exist in the landing repository.");
            string? targetCommit = await ResolveBranchCommitAsync(repository, target, token).ConfigureAwait(false);
            if (targetCommit == null) return Refuse(result, BranchWriteReasons.TargetMissing, "Target branch '" + target + "' does not exist in the landing repository.");
            result.SourceCommit = sourceCommit;
            result.PreviousTargetCommit = targetCommit;

            if (await IsBranchCheckedOutAsync(repository, target, token).ConfigureAwait(false))
                return Refuse(result, BranchWriteReasons.TargetCheckedOut, "Target branch '" + target + "' is checked out in a worktree of the landing repository; its index would no longer match the branch.");

            if (await IsAncestorAsync(repository, sourceCommit, targetCommit, token).ConfigureAwait(false))
                return Refuse(result, BranchWriteReasons.NothingToWrite, "Target branch '" + target + "' already contains '" + source + "'.");

            string newCommit;
            if (strategy == StrategyFastForward)
            {
                if (!await IsAncestorAsync(repository, targetCommit, sourceCommit, token).ConfigureAwait(false))
                    return Refuse(result, BranchWriteReasons.NonFastForward, "'" + target + "' is not an ancestor of '" + source + "'. Use MergeCommit to preserve both histories.");
                newCommit = sourceCommit;
            }
            else
            {
                GitRun tree = await RunGitAsync(repository, token, "merge-tree", "--write-tree", "--no-messages", targetCommit, sourceCommit).ConfigureAwait(false);
                if (tree.ExitCode == 1)
                    return Refuse(result, BranchWriteReasons.MergeConflict, "Merging '" + source + "' into '" + target + "' has content conflicts. Resolve them on a branch first.");
                string treeId = FirstLine(tree.Stdout);
                if (!tree.Ok || treeId.Length == 0)
                    return Refuse(result, BranchWriteReasons.GitFailed, "git merge-tree could not compute the merge.");

                List<string> commitArgs = await IdentityArgumentsAsync(repository, token).ConfigureAwait(false);
                commitArgs.AddRange(new[] { "commit-tree", treeId, "-p", targetCommit, "-p", sourceCommit, "-m", "Merge branch '" + source + "' into " + target });
                GitRun commit = await RunGitAsync(repository, token, commitArgs.ToArray()).ConfigureAwait(false);
                newCommit = FirstLine(commit.Stdout);
                if (!commit.Ok || newCommit.Length == 0)
                    return Refuse(result, BranchWriteReasons.GitFailed, "git commit-tree could not create the merge commit.");
            }

            GitRun update = await RunGitAsync(repository, token, "update-ref", "-m", "armada: operator branch merge", "refs/heads/" + target, newCommit, targetCommit).ConfigureAwait(false);
            if (!update.Ok)
                return Refuse(result, BranchWriteReasons.TargetMoved, "Target branch '" + target + "' moved while the merge was prepared. Nothing was changed; retry.");

            string? verifiedTip = await ResolveBranchCommitAsync(repository, target, token).ConfigureAwait(false);
            bool verified = String.Equals(verifiedTip, newCommit, StringComparison.Ordinal)
                && await IsAncestorAsync(repository, targetCommit, newCommit, token).ConfigureAwait(false)
                && await IsAncestorAsync(repository, sourceCommit, newCommit, token).ConfigureAwait(false);
            result.TargetCommit = verifiedTip;
            if (!verified)
                return Refuse(result, BranchWriteReasons.VerificationFailed, "Target branch '" + target + "' does not contain both the previous target and the source after the merge. Inspect the landing repository.");

            result.WorkingCheckoutSync = await SyncWorkingCheckoutAsync(vessel, repository, target, token).ConfigureAwait(false);
            result.Succeeded = true;
            result.Message = "Merged '" + source + "' into '" + target + "' (" + strategy + "). Nothing was pushed.";
            _Logging.Info(_Header + "vessel " + vessel.Id + " merged " + source + " into " + target + " at " + newCommit);
            return result;
        }

        /// <summary>
        /// Push one branch of the vessel landing repository to the vessel's origin without force.
        /// </summary>
        /// <param name="vessel">Vessel.</param>
        /// <param name="request">Explicit push request.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Verified result or a named refusal.</returns>
        public async Task<BranchWriteResult> PushAsync(Vessel vessel, BranchPushRequest? request, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            BranchWriteResult result = new BranchWriteResult
            {
                Operation = "push",
                VesselId = vessel.Id,
                SourceRef = request?.SourceRef,
                TargetRef = request?.TargetRef,
                Remote = request?.Remote
            };
            if (request == null || String.IsNullOrWhiteSpace(request.SourceRef) || String.IsNullOrWhiteSpace(request.TargetRef) || String.IsNullOrWhiteSpace(request.Remote))
                return Refuse(result, BranchWriteReasons.InvalidRequest, "SourceRef, TargetRef and Remote are required.");
            if (!String.Equals(request.Remote, AllowedRemote, StringComparison.Ordinal))
                return Refuse(result, BranchWriteReasons.RemoteNotAllowed, "Remote '" + request.Remote + "' is not allowed. Pushes go only to the vessel's configured origin.");

            string source = request.SourceRef;
            string target = request.TargetRef;
            bool publishesItself = String.Equals(source, target, StringComparison.Ordinal);
            BranchWriteResult? refusal = await ValidateCommonAsync(vessel, source, target, requireMissionGate: !publishesItself, result, token, allowSameRef: true).ConfigureAwait(false);
            if (refusal != null) return refusal;
            string repository = (await ResolveRepositoryAsync(vessel, token).ConfigureAwait(false))!;

            using IDisposable? lease = VesselRepositoryLock.TryAcquire(vessel.Id);
            if (lease == null)
                return Refuse(result, BranchWriteReasons.VesselBusy, "Another landing or branch write is in progress for this vessel. Retry when it finishes.");

            refusal = await ValidateWorkingCheckoutAsync(vessel, result, token).ConfigureAwait(false);
            if (refusal != null) return refusal;

            string? sourceCommit = await ResolveBranchCommitAsync(repository, source, token).ConfigureAwait(false);
            if (sourceCommit == null) return Refuse(result, BranchWriteReasons.SourceMissing, "Source branch '" + source + "' does not exist in the landing repository.");
            result.SourceCommit = sourceCommit;

            refusal = await VerifyOriginAsync(repository, vessel, result, token).ConfigureAwait(false);
            if (refusal != null) return refusal;

            GitRun remoteHead = await RunGitAsync(repository, token, "ls-remote", "--heads", AllowedRemote, "refs/heads/" + target).ConfigureAwait(false);
            if (!remoteHead.Ok)
                return Refuse(result, BranchWriteReasons.RemoteUnreadable, "The origin remote could not be read.");
            string? remoteCommit = ParseLsRemote(remoteHead.Stdout, "refs/heads/" + target);
            result.PreviousTargetCommit = remoteCommit;

            if (remoteCommit != null)
            {
                if (String.Equals(remoteCommit, sourceCommit, StringComparison.Ordinal))
                    return Refuse(result, BranchWriteReasons.NothingToWrite, "origin/" + target + " already points at '" + source + "'.");
                GitRun present = await RunGitAsync(repository, token, "cat-file", "-e", remoteCommit + "^{commit}").ConfigureAwait(false);
                if (!present.Ok)
                {
                    GitRun fetch = await RunGitAsync(repository, token, "fetch", "--no-tags", "--no-write-fetch-head", AllowedRemote, "refs/heads/" + target).ConfigureAwait(false);
                    if (!fetch.Ok)
                        return Refuse(result, BranchWriteReasons.RemoteUnreadable, "The current origin/" + target + " commit could not be fetched to check ancestry.");
                }
                if (!await IsAncestorAsync(repository, remoteCommit, sourceCommit, token).ConfigureAwait(false))
                    return Refuse(result, BranchWriteReasons.NonFastForward, "origin/" + target + " is not an ancestor of '" + source + "'. The push would rewrite remote history.");
            }

            GitRun push = await RunGitAsync(repository, token, "-c", "remote.origin.mirror=false", "push", "--porcelain", AllowedRemote, sourceCommit + ":refs/heads/" + target).ConfigureAwait(false);
            if (!push.Ok)
            {
                string combined = push.Stdout + "\n" + push.Stderr;
                if (combined.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) || combined.Contains("fetch first", StringComparison.OrdinalIgnoreCase))
                    return Refuse(result, BranchWriteReasons.NonFastForward, "origin rejected the push as a non-fast-forward update. Nothing was forced.");
                return Refuse(result, BranchWriteReasons.PushRejected, "origin rejected the push. Check remote permissions and branch protection.");
            }

            GitRun after = await RunGitAsync(repository, token, "ls-remote", "--heads", AllowedRemote, "refs/heads/" + target).ConfigureAwait(false);
            string? pushedCommit = after.Ok ? ParseLsRemote(after.Stdout, "refs/heads/" + target) : null;
            result.TargetCommit = pushedCommit;
            if (!String.Equals(pushedCommit, sourceCommit, StringComparison.Ordinal))
                return Refuse(result, BranchWriteReasons.VerificationFailed, "The push completed but origin/" + target + " does not point at the pushed commit.");

            result.Succeeded = true;
            result.Message = "Pushed '" + source + "' to origin/" + target + " without force.";
            _Logging.Info(_Header + "vessel " + vessel.Id + " pushed " + source + " to origin/" + target + " at " + sourceCommit);
            return result;
        }

        private async Task<BranchWriteResult?> ValidateCommonAsync(Vessel vessel, string source, string target, bool requireMissionGate, BranchWriteResult result, CancellationToken token, bool allowSameRef = false)
        {
            string? repository = await ResolveRepositoryAsync(vessel, token).ConfigureAwait(false);
            if (repository == null)
                return Refuse(result, BranchWriteReasons.RepositoryMissing, "The vessel landing repository (LocalPath) is missing or is not a git repository.");

            if (!await IsValidBranchNameAsync(repository, source, token).ConfigureAwait(false))
                return Refuse(result, BranchWriteReasons.InvalidRef, "SourceRef '" + source + "' is not a valid branch name. Use a short branch name.");
            if (!await IsValidBranchNameAsync(repository, target, token).ConfigureAwait(false))
                return Refuse(result, BranchWriteReasons.InvalidRef, "TargetRef '" + target + "' is not a valid branch name. Use a short branch name.");
            if (!allowSameRef && String.Equals(source, target, StringComparison.Ordinal))
                return Refuse(result, BranchWriteReasons.SameRef, "SourceRef and TargetRef name the same branch.");

            string? protectedMatch = LandingPreviewService.DetermineProtectedBranchMatch(vessel, target);
            if (!String.IsNullOrWhiteSpace(protectedMatch))
                return Refuse(result, BranchWriteReasons.ProtectedTarget, "Target branch '" + target + "' matches protected policy '" + protectedMatch + "'. Land it through the configured review path.");
            if (vessel.RequireMergeQueueForReleaseBranches
                && String.Equals(LandingPreviewService.DetermineBranchCategory(source, target, vessel.ReleaseBranchPrefix, vessel.HotfixBranchPrefix), "Release", StringComparison.OrdinalIgnoreCase))
                return Refuse(result, BranchWriteReasons.ReleaseRequiresMergeQueue, "This vessel requires release branches to land through the merge queue.");

            if (requireMissionGate)
            {
                List<Mission> missions = await _Database.Missions.EnumerateByVesselAsync(vessel.Id, token).ConfigureAwait(false);
                Mission? unlanded = missions.FirstOrDefault(m => String.Equals(m.BranchName, source, StringComparison.Ordinal) && m.Status != MissionStatusEnum.Complete);
                if (unlanded != null)
                    return Refuse(result, BranchWriteReasons.MissionBranchNotLanded, "'" + source + "' is the branch of mission " + unlanded.Id + " (" + unlanded.Status + "). Land it through its review and Check gates.");

                List<MergeEntry> entries = await _Database.MergeEntries.EnumerateAsync(token).ConfigureAwait(false);
                MergeEntry? active = entries.FirstOrDefault(e => String.Equals(e.VesselId, vessel.Id, StringComparison.Ordinal)
                    && String.Equals(e.BranchName, source, StringComparison.Ordinal)
                    && e.Status != MergeStatusEnum.Landed && e.Status != MergeStatusEnum.Failed && e.Status != MergeStatusEnum.Cancelled);
                if (active != null)
                    return Refuse(result, BranchWriteReasons.MergeQueueEntryActive, "'" + source + "' has merge-queue entry " + active.Id + " (" + active.Status + "). Let the merge queue land it.");
            }

            return null;
        }

        private async Task<BranchWriteResult?> ValidateWorkingCheckoutAsync(Vessel vessel, BranchWriteResult result, CancellationToken token)
        {
            string? working = vessel.WorkingDirectory;
            if (String.IsNullOrWhiteSpace(working)) return null;
            if (!Directory.Exists(working))
                return Refuse(result, BranchWriteReasons.WorkingCheckoutMissing, "The configured working checkout does not exist.");

            GitRun bare = await RunGitAsync(working, token, "rev-parse", "--is-bare-repository").ConfigureAwait(false);
            if (!bare.Ok)
                return Refuse(result, BranchWriteReasons.WorkingCheckoutMissing, "The configured working checkout is not a git repository.");
            if (String.Equals(FirstLine(bare.Stdout), "true", StringComparison.OrdinalIgnoreCase)) return null;

            GitRun head = await RunGitAsync(working, token, "symbolic-ref", "-q", "HEAD").ConfigureAwait(false);
            if (!head.Ok)
                return Refuse(result, BranchWriteReasons.WorkingCheckoutDetached, "The working checkout has a detached HEAD. Check out a branch first.");

            GitRun status = await RunGitAsync(working, token, "status", "--porcelain").ConfigureAwait(false);
            if (!status.Ok)
                return Refuse(result, BranchWriteReasons.GitFailed, "git status failed in the working checkout.");
            if (!String.IsNullOrWhiteSpace(status.Stdout))
                return Refuse(result, BranchWriteReasons.WorkingCheckoutDirty, "The working checkout has uncommitted changes. Commit or stash them first.");
            return null;
        }

        private async Task<BranchWriteResult?> VerifyOriginAsync(string repository, Vessel vessel, BranchWriteResult result, CancellationToken token)
        {
            GitRun url = await RunGitAsync(repository, token, "config", "--get", "remote.origin.url").ConfigureAwait(false);
            string originUrl = FirstLine(url.Stdout);
            if (!url.Ok || originUrl.Length == 0)
                return Refuse(result, BranchWriteReasons.RemoteMissing, "The landing repository has no origin remote.");
            if (String.IsNullOrWhiteSpace(vessel.RepoUrl) || !SameRemote(originUrl, vessel.RepoUrl))
                return Refuse(result, BranchWriteReasons.RemoteMismatch, "The landing repository origin does not match the vessel repository URL.");

            GitRun pushUrls = await RunGitAsync(repository, token, "config", "--get-all", "remote.origin.pushurl").ConfigureAwait(false);
            foreach (string pushUrl in pushUrls.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!SameRemote(pushUrl, vessel.RepoUrl))
                    return Refuse(result, BranchWriteReasons.RemoteMismatch, "The landing repository origin push URL does not match the vessel repository URL.");
            }
            return null;
        }

        private async Task<string> SyncWorkingCheckoutAsync(Vessel vessel, string repository, string target, CancellationToken token)
        {
            string? working = vessel.WorkingDirectory;
            if (String.IsNullOrWhiteSpace(working)) return "not_configured";
            if (String.Equals(Path.GetFullPath(working).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(repository).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
                return "same_as_landing_repository";

            GitRun bare = await RunGitAsync(working, token, "rev-parse", "--is-bare-repository").ConfigureAwait(false);
            if (String.Equals(FirstLine(bare.Stdout), "true", StringComparison.OrdinalIgnoreCase)) return "skipped_bare";

            GitRun head = await RunGitAsync(working, token, "symbolic-ref", "-q", "HEAD").ConfigureAwait(false);
            if (!String.Equals(FirstLine(head.Stdout), "refs/heads/" + target, StringComparison.Ordinal))
                return "skipped_on_other_branch";

            GitRun fetch = await RunGitAsync(working, token, "fetch", "--no-tags", repository, "refs/heads/" + target).ConfigureAwait(false);
            if (!fetch.Ok) return "failed_fetch";
            GitRun merge = await RunGitAsync(working, token, "merge", "--ff-only", "FETCH_HEAD").ConfigureAwait(false);
            if (!merge.Ok)
            {
                _Logging.Warn(_Header + "working checkout for vessel " + vessel.Id + " could not fast-forward to " + target);
                return "failed_not_fast_forward";
            }
            return "fast_forwarded";
        }

        private async Task<string?> ResolveRepositoryAsync(Vessel vessel, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(vessel.LocalPath) || !Directory.Exists(vessel.LocalPath)) return null;
            GitRun gitDir = await RunGitAsync(vessel.LocalPath, token, "rev-parse", "--git-dir").ConfigureAwait(false);
            return gitDir.Ok ? vessel.LocalPath : null;
        }

        private async Task<bool> IsValidBranchNameAsync(string repository, string name, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(name) || !String.Equals(name, name.Trim(), StringComparison.Ordinal)) return false;
            if (name.StartsWith("-", StringComparison.Ordinal) || name.StartsWith("refs/", StringComparison.Ordinal)) return false;
            if (String.Equals(name, "HEAD", StringComparison.Ordinal)) return false;
            GitRun check = await RunGitAsync(repository, token, "check-ref-format", "refs/heads/" + name).ConfigureAwait(false);
            return check.Ok;
        }

        private async Task<string?> ResolveBranchCommitAsync(string repository, string branch, CancellationToken token)
        {
            GitRun run = await RunGitAsync(repository, token, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch + "^{commit}").ConfigureAwait(false);
            string sha = FirstLine(run.Stdout);
            return run.Ok && sha.Length > 0 ? sha : null;
        }

        private async Task<bool> IsAncestorAsync(string repository, string ancestor, string descendant, CancellationToken token)
        {
            GitRun run = await RunGitAsync(repository, token, "merge-base", "--is-ancestor", ancestor, descendant).ConfigureAwait(false);
            if (run.ExitCode > 1)
                throw new InvalidOperationException("git merge-base --is-ancestor failed with exit code " + run.ExitCode + ".");
            return run.Ok;
        }

        private async Task<bool> IsBranchCheckedOutAsync(string repository, string branch, CancellationToken token)
        {
            GitRun list = await RunGitAsync(repository, token, "worktree", "list", "--porcelain").ConfigureAwait(false);
            if (!list.Ok)
                throw new InvalidOperationException("git worktree list failed with exit code " + list.ExitCode + ".");
            string expected = "branch refs/heads/" + branch;
            return list.Stdout.Split('\n', StringSplitOptions.TrimEntries).Any(line => String.Equals(line, expected, StringComparison.Ordinal));
        }

        private async Task<List<string>> IdentityArgumentsAsync(string repository, CancellationToken token)
        {
            List<string> args = new List<string>();
            GitRun name = await RunGitAsync(repository, token, "config", "--get", "user.name").ConfigureAwait(false);
            GitRun email = await RunGitAsync(repository, token, "config", "--get", "user.email").ConfigureAwait(false);
            if (!name.Ok || FirstLine(name.Stdout).Length == 0) args.AddRange(new[] { "-c", "user.name=Armada" });
            if (!email.Ok || FirstLine(email.Stdout).Length == 0) args.AddRange(new[] { "-c", "user.email=armada@users.noreply.invalid" });
            return args;
        }

        private static string? ParseLsRemote(string output, string fullRef)
        {
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = line.Split('\t');
                if (parts.Length == 2 && String.Equals(parts[1], fullRef, StringComparison.Ordinal)) return parts[0];
            }
            return null;
        }

        private static bool SameRemote(string left, string right)
        {
            return String.Equals(NormalizeRemote(left), NormalizeRemote(right), StringComparison.Ordinal);
        }

        private static string NormalizeRemote(string url)
        {
            string value = url.Trim().TrimEnd('/');
            if (value.EndsWith(".git", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 4);
            return value.TrimEnd('/');
        }

        private static string FirstLine(string text)
        {
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? String.Empty;
        }

        private BranchWriteResult Refuse(BranchWriteResult result, string reason, string message)
        {
            result.Succeeded = false;
            result.Reason = reason;
            result.Message = message;
            _Logging.Info(_Header + result.Operation + " refused for vessel " + result.VesselId + ": " + reason);
            return result;
        }

        private static async Task<GitRun> RunGitAsync(string workingDirectory, CancellationToken token, params string[] args)
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
            startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.EnvironmentVariables["GCM_INTERACTIVE"] = "Never";
            foreach (string arg in args) startInfo.ArgumentList.Add(arg);

            using CancellationTokenSource timeout = new CancellationTokenSource(GitProcessTimeouts.Resolve());
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            using Process process = new Process { StartInfo = startInfo };
            process.Start();
            try
            {
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
                Task<string> stderr = process.StandardError.ReadToEndAsync(linked.Token);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                return new GitRun { ExitCode = process.ExitCode, Stdout = stdout.Result, Stderr = stderr.Result };
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process already exited; there is nothing left to stop.
                }
                if (token.IsCancellationRequested) throw;
                throw new TimeoutException("git " + (args.Length > 0 ? args[0] : String.Empty) + " exceeded the git process timeout.");
            }
        }
    }
}
