namespace Armada.Core.Services
{
    using System.IO;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Service for managing mission landing operations including retries and dedicated worktree merges.
    /// </summary>
    public class LandingService : ILandingService
    {
        #region Public-Members

        /// <summary>
        /// Delegate invoked to perform the actual landing (push/PR/merge) for a mission.
        /// Wired by ArmadaServer to route through the existing HandleMissionCompleteAsync logic.
        /// </summary>
        public Func<Mission, Dock, Task>? OnPerformLanding { get; set; }

        #endregion

        #region Private-Members

        private string _Header = "[LandingService] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private IGitService _Git;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="git">Git service.</param>
        public LandingService(
            LoggingModule logging,
            DatabaseDriver database,
            ArmadaSettings settings,
            IGitService git)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Git = git ?? throw new ArgumentNullException(nameof(git));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<bool> MergeInDedicatedWorktreeAsync(
            Vessel vessel,
            Mission mission,
            string targetBranch,
            string? sourceBranch = null,
            string? commitMessage = null,
            CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            if (String.IsNullOrEmpty(vessel.LocalPath))
            {
                _Logging.Warn(_Header + "vessel " + vessel.Id + " has no LocalPath -- cannot merge mission " + mission.Id);
                return false;
            }

            if (String.IsNullOrEmpty(targetBranch))
            {
                _Logging.Warn(_Header + "mission " + mission.Id + " has no target branch -- cannot merge");
                return false;
            }

            string? missionBranch = !String.IsNullOrEmpty(sourceBranch) ? sourceBranch : mission.BranchName;
            if (String.IsNullOrEmpty(missionBranch))
            {
                _Logging.Warn(_Header + "mission " + mission.Id + " has no branch name -- cannot merge");
                return false;
            }

            string integrationRoot = Path.Combine(_Settings.DocksDirectory, "_integration");
            string worktreePath = Path.Combine(integrationRoot, mission.Id);
            Directory.CreateDirectory(integrationRoot);

            while (true)
            {
                bool succeeded = false;
                Exception? failure = null;

                try
                {
                    await _Git.FetchAsync(vessel.LocalPath, token).ConfigureAwait(false);

                    _Logging.Info(_Header + "creating integration worktree " + worktreePath + " for mission " + mission.Id + " target " + targetBranch + " attempt " + (mission.LandingRetryCount + 1));
                    await _Git.CreateWorktreeAsync(vessel.LocalPath, worktreePath, targetBranch, targetBranch, token: token).ConfigureAwait(false);

                    await _Git.MergeBranchLocalAsync(worktreePath, vessel.LocalPath, missionBranch, targetBranch, commitMessage, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "merged branch " + missionBranch + " into integration worktree " + worktreePath);

                    // LocalMerge lands into the local repository only -- pushing to a remote is
                    // the operator's decision, never automatic. Pushing here made every landing
                    // fail once the local default branch diverged from the remote: git rejects
                    // the push as non-fast-forward, IsTargetBranchDrift misclassifies that
                    // rejection as target-branch drift, and the retry loop re-merges and
                    // re-pushes the same divergence until maxLandingRetries is exhausted. The
                    // mission is then marked LandingFailed and its branch is left stray even
                    // though the local merge itself succeeded.
                    if (vessel.LandingMode != LandingModeEnum.LocalMerge)
                    {
                        await _Git.PushBranchAsync(worktreePath, "origin", token).ConfigureAwait(false);
                        _Logging.Info(_Header + "pushed merged changes from integration worktree " + worktreePath);
                    }
                    else
                    {
                        _Logging.Info(_Header + "landing mode is LocalMerge -- skipping push to origin for mission " + mission.Id);
                    }

                    succeeded = true;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "integration merge failed for mission " + mission.Id + " branch " + missionBranch + ": " + ex.Message);
                    failure = ex;
                    succeeded = false;
                }
                finally
                {
                    try
                    {
                        await _Git.RemoveWorktreeAsync(worktreePath, token).ConfigureAwait(false);
                        _Logging.Info(_Header + "removed integration worktree " + worktreePath);
                    }
                    catch (Exception removeEx)
                    {
                        _Logging.Warn(_Header + "failed to remove integration worktree " + worktreePath + ": " + removeEx.Message);
                    }

                    try
                    {
                        await _Git.PruneWorktreesAsync(vessel.LocalPath, token).ConfigureAwait(false);
                    }
                    catch (Exception pruneEx)
                    {
                        _Logging.Warn(_Header + "failed to prune integration worktrees for " + vessel.LocalPath + ": " + pruneEx.Message);
                    }
                }

                if (succeeded)
                {
                    await SyncUserWorkingDirectoryAfterLandingAsync(vessel, targetBranch, token).ConfigureAwait(false);
                    return true;
                }

                if (failure == null || !IsTargetBranchDrift(failure))
                {
                    return false;
                }

                int maxRetries = _Settings.MaxLandingRetries;
                if (mission.LandingRetryCount >= maxRetries)
                {
                    mission.FailureReason = "target_branch_drift_retry_exhausted: maxLandingRetries=" + maxRetries;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                    _Logging.Error(_Header + "target branch drift retry exhausted for mission " + mission.Id + " after " + mission.LandingRetryCount + " retries");
                    return false;
                }

                mission.LandingRetryCount++;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await PersistMissionRetryStateAsync(mission, token).ConfigureAwait(false);
                _Logging.Warn(_Header + "target branch drift detected for mission " + mission.Id + "; auto-rebasing and retrying landing attempt " + mission.LandingRetryCount + " of " + maxRetries);
            }
        }

        /// <inheritdoc />
        public async Task<bool> RetryLandingAsync(string missionId, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));

            Mission? mission = !String.IsNullOrEmpty(tenantId)
                ? await _Database.Missions.ReadAsync(tenantId, missionId, token).ConfigureAwait(false)
                : await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
            if (mission == null)
            {
                _Logging.Warn(_Header + "mission " + missionId + " not found");
                return false;
            }

            if (mission.Status != MissionStatusEnum.LandingFailed && mission.Status != MissionStatusEnum.WorkProduced)
            {
                _Logging.Warn(_Header + "mission " + missionId + " is in status " + mission.Status + ", not LandingFailed or WorkProduced -- cannot retry");
                return false;
            }

            if (String.IsNullOrEmpty(mission.VesselId))
            {
                _Logging.Warn(_Header + "mission " + missionId + " has no vessel -- cannot retry landing");
                return false;
            }

            // Use the mission's own TenantId for vessel lookup (more reliable than caller's tenantId)
            string? lookupTenantId = !String.IsNullOrEmpty(mission.TenantId) ? mission.TenantId : tenantId;
            Vessel? vessel = !String.IsNullOrEmpty(lookupTenantId)
                ? await _Database.Vessels.ReadAsync(lookupTenantId, mission.VesselId, token).ConfigureAwait(false)
                : await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
            // Fall back to unscoped read if tenant-scoped read fails
            if (vessel == null)
                vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
            if (vessel == null)
            {
                _Logging.Warn(_Header + "vessel " + mission.VesselId + " not found -- cannot retry landing for mission " + missionId);
                return false;
            }

            if (String.IsNullOrEmpty(vessel.LocalPath))
            {
                _Logging.Warn(_Header + "vessel " + vessel.Id + " has no LocalPath -- cannot retry landing for mission " + missionId);
                return false;
            }

            if (String.IsNullOrEmpty(mission.BranchName))
            {
                _Logging.Warn(_Header + "mission " + missionId + " has no branch name -- cannot retry landing");
                return false;
            }

            // Emit retry event
            try
            {
                ArmadaEvent retryEvent = new ArmadaEvent("mission.landing_retry", "Retrying landing: " + mission.Title);
                retryEvent.EntityType = "mission";
                retryEvent.EntityId = mission.Id;
                retryEvent.MissionId = mission.Id;
                retryEvent.VesselId = mission.VesselId;
                retryEvent.VoyageId = mission.VoyageId;
                await _Database.Events.CreateAsync(retryEvent, token).ConfigureAwait(false);
            }
            catch { }

            // Attempt rebase of mission branch onto current target branch
            string repoPath = vessel.LocalPath;
            string targetBranch = vessel.DefaultBranch;
            string missionBranch = mission.BranchName;

            try
            {
                // Fetch latest from remote
                await _Git.FetchAsync(repoPath, token).ConfigureAwait(false);

                // Check if branch still exists
                bool branchExists = await _Git.BranchExistsAsync(repoPath, missionBranch, token).ConfigureAwait(false);
                if (!branchExists)
                {
                    _Logging.Warn(_Header + "branch " + missionBranch + " no longer exists -- cannot retry landing for mission " + missionId);
                    return false;
                }

                _Logging.Info(_Header + "retrying landing for mission " + missionId + " branch " + missionBranch);

                // Transition back to WorkProduced for landing attempt
                mission.Status = MissionStatusEnum.WorkProduced;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                // Try to find the dock or create a temporary context for landing
                Dock? dock = null;
                if (!String.IsNullOrEmpty(mission.DockId))
                {
                    dock = !String.IsNullOrEmpty(tenantId)
                        ? await _Database.Docks.ReadAsync(tenantId, mission.DockId, token).ConfigureAwait(false)
                        : await _Database.Docks.ReadAsync(mission.DockId, token).ConfigureAwait(false);
                }

                // If no dock, create a minimal one for the landing handler
                if (dock == null)
                {
                    dock = new Dock(vessel.Id);
                    dock.BranchName = missionBranch;
                    dock.WorktreePath = vessel.WorkingDirectory ?? vessel.LocalPath;
                    dock.Active = false; // Not a real provisioned dock
                }

                // Invoke the landing handler if available
                if (OnPerformLanding != null)
                {
                    await OnPerformLanding.Invoke(mission, dock).ConfigureAwait(false);
                    _Logging.Info(_Header + "landing retry completed for mission " + missionId);

                    // Re-read mission to get updated status from landing handler
                    mission = !String.IsNullOrEmpty(tenantId)
                        ? await _Database.Missions.ReadAsync(tenantId, missionId, token).ConfigureAwait(false)
                        : await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
                    return mission != null && mission.Status == MissionStatusEnum.Complete;
                }
                else
                {
                    _Logging.Warn(_Header + "no landing handler configured -- cannot retry landing for mission " + missionId);
                    mission.Status = MissionStatusEnum.LandingFailed;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "landing retry failed for mission " + missionId + ": " + ex.Message);

                // Ensure mission goes back to LandingFailed
                try
                {
                    mission = !String.IsNullOrEmpty(tenantId)
                        ? await _Database.Missions.ReadAsync(tenantId, missionId, token).ConfigureAwait(false)
                        : await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
                    if (mission != null && mission.Status != MissionStatusEnum.LandingFailed)
                    {
                        mission.Status = MissionStatusEnum.LandingFailed;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
                    }
                }
                catch { }

                return false;
            }
        }

        #endregion

        #region Private-Methods

        private async Task SyncUserWorkingDirectoryAfterLandingAsync(Vessel vessel, string targetBranch, CancellationToken token)
        {
            if (String.IsNullOrEmpty(vessel.WorkingDirectory))
            {
                return;
            }

            try
            {
                bool isClean = await _Git.IsWorkingDirectoryCleanAsync(vessel.WorkingDirectory, token).ConfigureAwait(false);
                string? currentBranch = await _Git.GetCurrentBranchAsync(vessel.WorkingDirectory, token).ConfigureAwait(false);

                if (isClean && String.Equals(currentBranch, targetBranch, StringComparison.Ordinal))
                {
                    await _Git.PullFastForwardOnlyAsync(vessel.WorkingDirectory, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "synced user working directory " + vessel.WorkingDirectory + " with fast-forward pull");
                }
                else
                {
                    _Logging.Info(_Header + "leaving user working directory " + vessel.WorkingDirectory + " unchanged; run git pull on " + targetBranch + " to sync when ready");
                }
            }
            catch (Exception ex)
            {
                _Logging.Info(_Header + "leaving user working directory " + vessel.WorkingDirectory + " unchanged after sync check failed: " + ex.Message + "; run git pull on " + targetBranch + " to sync when ready");
            }
        }

        private async Task PersistMissionRetryStateAsync(Mission mission, CancellationToken token)
        {
            try
            {
                await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to persist landing retry state for mission " + mission.Id + ": " + ex.Message);
            }
        }

        private static bool IsTargetBranchDrift(Exception ex)
        {
            string message = ex.Message ?? String.Empty;
            return message.Contains("target branch drift", StringComparison.OrdinalIgnoreCase)
                || message.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
                || message.Contains("fetch first", StringComparison.OrdinalIgnoreCase)
                || message.Contains("stale info", StringComparison.OrdinalIgnoreCase)
                || message.Contains("failed to push some refs", StringComparison.OrdinalIgnoreCase)
                || message.Contains("remote contains work", StringComparison.OrdinalIgnoreCase)
                || message.Contains("tip of your current branch is behind", StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
