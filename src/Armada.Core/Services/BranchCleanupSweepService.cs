namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Maintenance sweep that self-heals ref accumulation on every vessel. It removes Armada-owned
    /// branches whose tip is already an ancestor of the default branch, from the vessel bare and,
    /// under LocalAndRemote, from origin, and it removes landed preserved refs once they are older
    /// than the retention window. Unlanded work is never removed. Every run logs one summary line
    /// with its counts and the reason for each skipped vessel, including runs that removed nothing.
    /// </summary>
    /// <remarks>
    /// Rules the sweep holds:
    /// <list type="bullet">
    /// <item>Only the "armada/" and "armada-landing/" branch namespaces, refs/armada-preserved/, and the
    /// reclaim anchors refs/armada/docks/ and refs/armada/missions/ are candidates. The two branch
    /// prefixes diverge at the character after "armada", so both are listed; recover/ refs, human
    /// branches and every other ref family are never touched.</item>
    /// <item>A reclaim anchor is kept while the dock or mission it names is live, while a recover/
    /// branch points at the same commit, while its tip is unlanded, and inside the preserved-ref
    /// retention window. Recover tips come from the vessel bare and, when the sweep lists origin, from
    /// origin.</item>
    /// <item>Origin is enumerated on its own. A landing can delete the bare copy of a branch while the
    /// origin copy survives, so deciding remote deletions from the bare's branch list misses them.</item>
    /// <item>"Landed" is decided by commit: the ref's tip must be an ancestor of the default branch in the
    /// vessel bare. A tip the bare does not hold reads as unlanded and is kept.</item>
    /// <item>A branch named by a non-terminal mission is kept even when it reads as landed: a freshly
    /// provisioned mission branch sits at the default-branch tip before its captain commits.</item>
    /// <item>Deletions of refs the sweep read are compare-and-swap on the tip it measured, so a ref that
    /// moved since is refused rather than deleted.</item>
    /// </list>
    /// </remarks>
    public class BranchCleanupSweepService
    {
        #region Private-Members

        private const string _BranchRefPrefix = "refs/heads/";
        private const string _PreservedRefPrefix = "refs/armada-preserved/";
        private const string _DockAnchorPrefix = "refs/armada/docks/";
        private const string _MissionAnchorPrefix = "refs/armada/missions/";
        private const string _RecoverBranchPrefix = "refs/heads/recover/";
        private const string _RemoteName = "origin";

        /// <summary>
        /// Branch namespaces Armada creates and is therefore allowed to reap. Both are required:
        /// captain branches use the first, landing branches the second, and neither prefix matches
        /// the other.
        /// </summary>
        private static readonly string[] _ManagedBranchPrefixes = new string[]
        {
            "armada/",
            "armada-landing/"
        };

        private string _Header = "[BranchCleanupSweepService] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private IGitService _Git;
        private IBranchInventory _Inventory;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="git">Git service; must also implement <see cref="IBranchInventory"/>.</param>
        public BranchCleanupSweepService(LoggingModule logging, DatabaseDriver database, ArmadaSettings settings, IGitService git)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
            _Inventory = git as IBranchInventory
                ?? throw new InvalidOperationException("IGitService implementation must also implement IBranchInventory");
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Run one maintenance sweep across every vessel. Returns the run's counts; per-vessel and
        /// per-ref failures are counted, logged and never fatal to the rest of the sweep.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Sweep result, including the summary line that was logged.</returns>
        public async Task<BranchCleanupSweepResult> SweepAsync(CancellationToken token = default)
        {
            BranchCleanupSweepResult result = new BranchCleanupSweepResult();

            List<Vessel> vessels;
            try
            {
                vessels = await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                result.Cancelled = true;
                return Complete(result);
            }
            catch (Exception ex)
            {
                result.VesselErrors++;
                result.SkipReasons.Add("all vessels: could not enumerate vessels: " + ex.Message);
                return Complete(result);
            }

            foreach (Vessel vessel in vessels)
            {
                if (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    break;
                }

                BranchCleanupPolicyEnum policy = vessel.BranchCleanupPolicy ?? _Settings.BranchCleanupPolicy;
                string? skipReason = GetVesselSkipReason(vessel, policy);
                if (skipReason != null)
                {
                    result.SkippedVessels++;
                    RecordSkip(result, vessel.Id, skipReason);
                    continue;
                }

                result.SweptVessels++;
                try
                {
                    await SweepVesselAsync(vessel, policy, result, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    result.VesselErrors++;
                    _Logging.Warn(_Header + "sweep failed for vessel " + vessel.Id + ": " + ex.Message);
                }
            }

            return Complete(result);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// True when the branch sits in a namespace Armada creates. Anything else -- a human branch,
        /// a salvage branch, a recover branch, a release branch -- is never a sweep candidate.
        /// </summary>
        internal static bool IsManagedBranch(string branch)
        {
            if (String.IsNullOrWhiteSpace(branch))
            {
                return false;
            }

            foreach (string prefix in _ManagedBranchPrefixes)
            {
                if (branch.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static string? GetVesselSkipReason(Vessel vessel, BranchCleanupPolicyEnum policy)
        {
            if (String.IsNullOrWhiteSpace(vessel.LocalPath))
            {
                return "no local repository configured";
            }

            if (!Directory.Exists(vessel.LocalPath))
            {
                return "no local repository at " + vessel.LocalPath;
            }

            if (policy == BranchCleanupPolicyEnum.None)
            {
                return "branch cleanup policy None";
            }

            return null;
        }

        private void RecordSkip(BranchCleanupSweepResult result, string vesselId, string reason)
        {
            result.SkipReasons.Add(vesselId + ": " + reason);
            _Logging.Info(_Header + "vessel " + vesselId + " skipped: " + reason);
        }

        private async Task SweepVesselAsync(Vessel vessel, BranchCleanupPolicyEnum policy, BranchCleanupSweepResult result, CancellationToken token)
        {
            string repoPath = vessel.LocalPath!;
            string defaultBranch = String.IsNullOrWhiteSpace(vessel.DefaultBranch) ? "main" : vessel.DefaultBranch!;
            string defaultRef = _BranchRefPrefix + defaultBranch;

            // Ancestry reads "not landed" for a missing ref, so a missing default branch would keep
            // every candidate while reporting a healthy run. Refuse the vessel with a reason instead.
            if (!await _Inventory.IsAncestorAsync(repoPath, defaultRef, defaultRef, token).ConfigureAwait(false))
            {
                throw new InvalidOperationException("default branch " + defaultBranch + " is not present in " + repoPath);
            }

            List<Mission> missions = await _Database.Missions.EnumerateByVesselAsync(vessel.Id, token).ConfigureAwait(false);
            HashSet<string> activeBranches = BuildActiveMissionBranches(missions);
            HashSet<string> liveMissionIds = BuildLiveMissionIds(missions);
            HashSet<string> liveDockIds = await ReadLiveDockIdsAsync(vessel, missions, token).ConfigureAwait(false);
            DateTime? preservedCutoffUtc = _Settings.BranchCleanupPreservedRefRetentionDays > 0
                ? DateTime.UtcNow.AddDays(-_Settings.BranchCleanupPreservedRefRetentionDays)
                : (DateTime?)null;

            // Origin is listed before any local deletion so that a recover/ branch that exists only on
            // origin still protects the anchors at its tip on both sides.
            IReadOnlyList<GitRefTip>? remoteTips = await TryListOriginAsync(vessel, policy, result, token).ConfigureAwait(false);
            HashSet<string> recoverTips = await ReadRecoverTipsAsync(repoPath, remoteTips, token).ConfigureAwait(false);

            IReadOnlyList<GitRefTip> localBranches = await _Inventory.EnumerateRefTipsAsync(repoPath, _BranchRefPrefix, token).ConfigureAwait(false);
            foreach (GitRefTip tip in localBranches)
            {
                token.ThrowIfCancellationRequested();
                string branch = tip.RefName.Substring(_BranchRefPrefix.Length);
                if (!IsSweepCandidate(branch, defaultBranch)) continue;

                result.LocalCandidates++;
                if (!await IsRemovableBranchAsync(vessel, repoPath, branch, tip.CommitSha, defaultRef, activeBranches, result, token).ConfigureAwait(false))
                {
                    continue;
                }

                try
                {
                    // branch -D refuses a branch checked out in a dock worktree, which keeps a live dock intact.
                    await _Git.DeleteLocalBranchAsync(repoPath, branch, token).ConfigureAwait(false);
                    result.SweptLocal++;
                    await EmitSweptEventAsync(vessel, branch, true, false, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    _Logging.Warn(_Header + "failed to delete landed branch " + branch + " from vessel " + vessel.Id + ": " + ex.Message);
                }
            }

            IReadOnlyList<GitRefTip> localPreserved = await _Inventory.EnumerateRefTipsAsync(repoPath, _PreservedRefPrefix, token).ConfigureAwait(false);
            foreach (GitRefTip tip in localPreserved)
            {
                token.ThrowIfCancellationRequested();
                result.PreservedCandidates++;
                if (!await IsPrunablePreservedRefAsync(vessel, repoPath, tip, tip.CommitUtc, defaultRef, preservedCutoffUtc, activeBranches, result, token).ConfigureAwait(false))
                {
                    continue;
                }

                try
                {
                    await _Inventory.DeleteRefIfAtAsync(repoPath, tip.RefName, tip.CommitSha, token).ConfigureAwait(false);
                    result.SweptPreservedLocal++;
                    await EmitSweptEventAsync(vessel, tip.RefName, true, false, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    _Logging.Warn(_Header + "failed to delete landed preserved ref " + tip.RefName + " from vessel " + vessel.Id + ": " + ex.Message);
                }
            }

            await SweepLocalAnchorsAsync(vessel, repoPath, _DockAnchorPrefix, liveDockIds, recoverTips, defaultRef, preservedCutoffUtc, result.DockAnchors, result, token).ConfigureAwait(false);
            await SweepLocalAnchorsAsync(vessel, repoPath, _MissionAnchorPrefix, liveMissionIds, recoverTips, defaultRef, preservedCutoffUtc, result.MissionAnchors, result, token).ConfigureAwait(false);

            if (remoteTips == null)
            {
                return;
            }

            foreach (GitRefTip tip in remoteTips)
            {
                token.ThrowIfCancellationRequested();
                if (tip.RefName.StartsWith(_BranchRefPrefix, StringComparison.Ordinal))
                {
                    string branch = tip.RefName.Substring(_BranchRefPrefix.Length);
                    if (!IsSweepCandidate(branch, defaultBranch)) continue;

                    result.RemoteCandidates++;
                    if (!await IsRemovableBranchAsync(vessel, repoPath, branch, tip.CommitSha, defaultRef, activeBranches, result, token).ConfigureAwait(false))
                    {
                        continue;
                    }

                    await DeleteRemoteRefAsync(vessel, tip, branch, () => result.SweptRemote++, result, token).ConfigureAwait(false);
                }
                else if (tip.RefName.StartsWith(_PreservedRefPrefix, StringComparison.Ordinal))
                {
                    result.PreservedCandidates++;
                    DateTime? commitUtc = await _Inventory.TryGetCommitTimeUtcAsync(repoPath, tip.CommitSha, token).ConfigureAwait(false);
                    if (!await IsPrunablePreservedRefAsync(vessel, repoPath, tip, commitUtc, defaultRef, preservedCutoffUtc, activeBranches, result, token).ConfigureAwait(false))
                    {
                        continue;
                    }

                    await DeleteRemoteRefAsync(vessel, tip, tip.RefName, () => result.SweptPreservedRemote++, result, token).ConfigureAwait(false);
                }
                else if (tip.RefName.StartsWith(_DockAnchorPrefix, StringComparison.Ordinal))
                {
                    await SweepRemoteAnchorAsync(vessel, repoPath, tip, _DockAnchorPrefix, liveDockIds, recoverTips, defaultRef, preservedCutoffUtc, result.DockAnchors, result, token).ConfigureAwait(false);
                }
                else if (tip.RefName.StartsWith(_MissionAnchorPrefix, StringComparison.Ordinal))
                {
                    await SweepRemoteAnchorAsync(vessel, repoPath, tip, _MissionAnchorPrefix, liveMissionIds, recoverTips, defaultRef, preservedCutoffUtc, result.MissionAnchors, result, token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Lists origin for a LocalAndRemote vessel. Returns null, with the reason recorded, when the
        /// policy does not reach origin or origin cannot be listed.
        /// </summary>
        private async Task<IReadOnlyList<GitRefTip>?> TryListOriginAsync(Vessel vessel, BranchCleanupPolicyEnum policy, BranchCleanupSweepResult result, CancellationToken token)
        {
            if (policy != BranchCleanupPolicyEnum.LocalAndRemote)
            {
                return null;
            }

            if (String.IsNullOrWhiteSpace(vessel.WorkingDirectory) || !Directory.Exists(vessel.WorkingDirectory))
            {
                RecordSkip(result, vessel.Id, "origin sweep skipped: no working checkout to reach origin");
                return null;
            }

            try
            {
                return await _Inventory.EnumerateRemoteRefTipsAsync(vessel.WorkingDirectory!, _RemoteName, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.VesselErrors++;
                RecordSkip(result, vessel.Id, "origin sweep skipped: could not list origin refs: " + ex.Message);
                return null;
            }
        }

        private async Task<HashSet<string>> ReadRecoverTipsAsync(string repoPath, IReadOnlyList<GitRefTip>? remoteTips, CancellationToken token)
        {
            HashSet<string> tips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<GitRefTip> local = await _Inventory.EnumerateRefTipsAsync(repoPath, _RecoverBranchPrefix, token).ConfigureAwait(false);
            foreach (GitRefTip tip in local)
            {
                tips.Add(tip.CommitSha);
            }

            if (remoteTips != null)
            {
                foreach (GitRefTip tip in remoteTips)
                {
                    if (tip.RefName.StartsWith(_RecoverBranchPrefix, StringComparison.Ordinal))
                    {
                        tips.Add(tip.CommitSha);
                    }
                }
            }

            return tips;
        }

        private async Task SweepLocalAnchorsAsync(
            Vessel vessel,
            string repoPath,
            string prefix,
            HashSet<string> liveIds,
            HashSet<string> recoverTips,
            string defaultRef,
            DateTime? cutoffUtc,
            BranchCleanupAnchorFamilyCounts counts,
            BranchCleanupSweepResult result,
            CancellationToken token)
        {
            IReadOnlyList<GitRefTip> anchors = await _Inventory.EnumerateRefTipsAsync(repoPath, prefix, token).ConfigureAwait(false);
            foreach (GitRefTip tip in anchors)
            {
                token.ThrowIfCancellationRequested();
                if (!await IsPrunableAnchorAsync(vessel, repoPath, tip, prefix, tip.CommitUtc, liveIds, recoverTips, defaultRef, cutoffUtc, counts, result, token).ConfigureAwait(false))
                {
                    continue;
                }

                try
                {
                    await _Inventory.DeleteRefIfAtAsync(repoPath, tip.RefName, tip.CommitSha, token).ConfigureAwait(false);
                    counts.SweptLocal++;
                    await EmitSweptEventAsync(vessel, tip.RefName, true, false, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    _Logging.Warn(_Header + "failed to delete landed anchor " + tip.RefName + " from vessel " + vessel.Id + ": " + ex.Message);
                }
            }
        }

        private async Task SweepRemoteAnchorAsync(
            Vessel vessel,
            string repoPath,
            GitRefTip tip,
            string prefix,
            HashSet<string> liveIds,
            HashSet<string> recoverTips,
            string defaultRef,
            DateTime? cutoffUtc,
            BranchCleanupAnchorFamilyCounts counts,
            BranchCleanupSweepResult result,
            CancellationToken token)
        {
            // A remote listing carries no commit time; the bare holds every landed commit.
            DateTime? commitUtc = await _Inventory.TryGetCommitTimeUtcAsync(repoPath, tip.CommitSha, token).ConfigureAwait(false);
            if (!await IsPrunableAnchorAsync(vessel, repoPath, tip, prefix, commitUtc, liveIds, recoverTips, defaultRef, cutoffUtc, counts, result, token).ConfigureAwait(false))
            {
                return;
            }

            await DeleteRemoteRefAsync(vessel, tip, tip.RefName, () => counts.SweptRemote++, result, token).ConfigureAwait(false);
        }

        /// <summary>
        /// The one retention rule for both reclaim anchor families. Each candidate is counted in
        /// exactly one kept bucket unless it is removable or its ancestry check fails.
        /// </summary>
        private async Task<bool> IsPrunableAnchorAsync(
            Vessel vessel,
            string repoPath,
            GitRefTip tip,
            string prefix,
            DateTime? commitUtc,
            HashSet<string> liveIds,
            HashSet<string> recoverTips,
            string defaultRef,
            DateTime? cutoffUtc,
            BranchCleanupAnchorFamilyCounts counts,
            BranchCleanupSweepResult result,
            CancellationToken token)
        {
            counts.Candidates++;

            string ownerId = tip.RefName.Substring(prefix.Length);
            if (liveIds.Contains(ownerId))
            {
                counts.KeptActive++;
                return false;
            }

            if (recoverTips.Contains(tip.CommitSha))
            {
                counts.KeptRecoverPointer++;
                return false;
            }

            bool? landed = await TryIsLandedAsync(vessel, repoPath, tip.RefName, tip.CommitSha, defaultRef, result, token).ConfigureAwait(false);
            if (landed == null) return false;
            if (!landed.Value)
            {
                counts.KeptUnlanded++;
                return false;
            }

            if (cutoffUtc == null || commitUtc == null || commitUtc.Value > cutoffUtc.Value)
            {
                counts.KeptInRetention++;
                return false;
            }

            return true;
        }

        private static bool IsSweepCandidate(string branch, string defaultBranch)
        {
            if (String.Equals(branch, defaultBranch, StringComparison.OrdinalIgnoreCase)) return false;
            return IsManagedBranch(branch);
        }

        private async Task<bool> IsRemovableBranchAsync(
            Vessel vessel,
            string repoPath,
            string branch,
            string commitSha,
            string defaultRef,
            HashSet<string> activeBranches,
            BranchCleanupSweepResult result,
            CancellationToken token)
        {
            if (activeBranches.Contains(branch))
            {
                result.KeptActive++;
                return false;
            }

            bool? landed = await TryIsLandedAsync(vessel, repoPath, branch, commitSha, defaultRef, result, token).ConfigureAwait(false);
            if (landed == null) return false;
            if (!landed.Value)
            {
                result.KeptUnmerged++;
                return false;
            }

            result.Merged++;
            return true;
        }

        private async Task<bool> IsPrunablePreservedRefAsync(
            Vessel vessel,
            string repoPath,
            GitRefTip tip,
            DateTime? commitUtc,
            string defaultRef,
            DateTime? cutoffUtc,
            HashSet<string> activeBranches,
            BranchCleanupSweepResult result,
            CancellationToken token)
        {
            string branch = tip.RefName.Substring(_PreservedRefPrefix.Length);
            if (activeBranches.Contains(branch))
            {
                result.KeptActive++;
                return false;
            }

            bool? landed = await TryIsLandedAsync(vessel, repoPath, tip.RefName, tip.CommitSha, defaultRef, result, token).ConfigureAwait(false);
            if (landed == null) return false;
            if (!landed.Value)
            {
                // Unlanded work stays reachable by name however old it is.
                result.KeptPreservedUnlanded++;
                return false;
            }

            // Retention zero, an unreadable commit time, or a tip inside the window all keep the ref.
            if (cutoffUtc == null || commitUtc == null || commitUtc.Value > cutoffUtc.Value)
            {
                result.KeptPreservedInRetention++;
                return false;
            }

            return true;
        }

        private async Task<bool?> TryIsLandedAsync(
            Vessel vessel,
            string repoPath,
            string refName,
            string commitSha,
            string defaultRef,
            BranchCleanupSweepResult result,
            CancellationToken token)
        {
            try
            {
                return await _Inventory.IsAncestorAsync(repoPath, commitSha, defaultRef, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Failed++;
                _Logging.Warn(_Header + "ancestry check failed for " + refName + " on vessel " + vessel.Id + ": " + ex.Message);
                return null;
            }
        }

        private async Task DeleteRemoteRefAsync(Vessel vessel, GitRefTip tip, string displayName, Action onDeleted, BranchCleanupSweepResult result, CancellationToken token)
        {
            try
            {
                await _Inventory.DeleteRemoteRefIfAtAsync(vessel.WorkingDirectory!, _RemoteName, tip.RefName, tip.CommitSha, token).ConfigureAwait(false);
                onDeleted();
                await EmitSweptEventAsync(vessel, displayName, false, true, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Failed++;
                _Logging.Warn(_Header + "failed to delete " + tip.RefName + " from origin for vessel " + vessel.Id + ": " + ex.Message);
            }
        }

        private static HashSet<string> BuildActiveMissionBranches(List<Mission> missions)
        {
            HashSet<string> branches = new HashSet<string>(StringComparer.Ordinal);
            foreach (Mission mission in missions)
            {
                if (String.IsNullOrWhiteSpace(mission.BranchName)) continue;
                if (MissionStateMachine.IsTerminal(mission.Status)) continue;
                branches.Add(mission.BranchName!.Trim());
            }
            return branches;
        }

        private static HashSet<string> BuildLiveMissionIds(List<Mission> missions)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Mission mission in missions)
            {
                if (MissionStateMachine.IsTerminal(mission.Status)) continue;
                ids.Add(mission.Id);
            }
            return ids;
        }

        /// <summary>
        /// A dock is live while its record is active or while a non-terminal mission names it.
        /// </summary>
        private async Task<HashSet<string>> ReadLiveDockIdsAsync(Vessel vessel, List<Mission> missions, CancellationToken token)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (Mission mission in missions)
            {
                if (MissionStateMachine.IsTerminal(mission.Status)) continue;
                if (String.IsNullOrWhiteSpace(mission.DockId)) continue;
                ids.Add(mission.DockId!.Trim());
            }

            List<Dock> docks = await _Database.Docks.EnumerateByVesselAsync(vessel.Id, token).ConfigureAwait(false);
            foreach (Dock dock in docks)
            {
                if (dock.Active) ids.Add(dock.Id);
            }
            return ids;
        }

        private BranchCleanupSweepResult Complete(BranchCleanupSweepResult result)
        {
            result.Summary = BuildSummary(result);
            if (result.Failed > 0 || result.VesselErrors > 0)
            {
                _Logging.Warn(_Header + result.Summary);
            }
            else
            {
                _Logging.Info(_Header + result.Summary);
            }
            return result;
        }

        internal static string BuildSummary(BranchCleanupSweepResult result)
        {
            string summary = "sweep complete: vessels swept " + result.SweptVessels
                + ", skipped " + result.SkippedVessels
                + ", errors " + result.VesselErrors
                + "; branches: local candidates " + result.LocalCandidates
                + ", origin candidates " + result.RemoteCandidates
                + ", landed " + result.Merged
                + ", kept unlanded " + result.KeptUnmerged
                + ", kept for active missions " + result.KeptActive
                + ", removed local " + result.SweptLocal
                + ", removed origin " + result.SweptRemote
                + "; preserved refs: candidates " + result.PreservedCandidates
                + ", kept unlanded " + result.KeptPreservedUnlanded
                + ", kept in retention " + result.KeptPreservedInRetention
                + ", removed local " + result.SweptPreservedLocal
                + ", removed origin " + result.SweptPreservedRemote
                + DescribeAnchorFamily("dock anchors", result.DockAnchors)
                + DescribeAnchorFamily("mission anchors", result.MissionAnchors)
                + "; failed operations " + result.Failed;

            if (result.SkipReasons.Count > 0)
            {
                summary += "; skips: " + String.Join(" | ", result.SkipReasons);
            }

            if (result.Cancelled)
            {
                summary += "; cancelled before every vessel was swept";
            }

            return summary;
        }

        private static string DescribeAnchorFamily(string label, BranchCleanupAnchorFamilyCounts counts)
        {
            return "; " + label + ": candidates " + counts.Candidates
                + ", kept for active missions " + counts.KeptActive
                + ", kept for recover pointers " + counts.KeptRecoverPointer
                + ", kept unlanded " + counts.KeptUnlanded
                + ", kept in retention " + counts.KeptInRetention
                + ", removed local " + counts.SweptLocal
                + ", removed origin " + counts.SweptRemote;
        }

        private async Task EmitSweptEventAsync(Vessel vessel, string name, bool localOk, bool remoteOk, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent
                {
                    EventType = "branch_cleanup.swept",
                    EntityType = "vessel",
                    EntityId = vessel.Id,
                    VesselId = vessel.Id,
                    Message = "Maintenance sweep removed landed ref " + name + " (local: " + localOk + ", remote: " + remoteOk + ")",
                    Payload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        vesselId = vessel.Id,
                        branch = name,
                        localDeleted = localOk,
                        remoteDeleted = remoteOk
                    })
                };
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not record branch sweep event: " + ex.Message);
            }
        }

        #endregion
    }
}
