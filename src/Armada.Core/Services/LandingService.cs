namespace Armada.Core.Services
{
    using System.Collections.Concurrent;
    using System.Text.Json;
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

        // Per-mission auto-retry attempt counts. In-memory by design (no DB schema change):
        // cleared when a mission reaches a terminal landing outcome (Complete or exhausted).
        private readonly ConcurrentDictionary<string, int> _AutoRetryCounts = new ConcurrentDictionary<string, int>();

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
        public async Task<bool> RetryLandingAsync(string missionId, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));

            LandingRetryContext? ctx = await ResolveContextAsync(missionId, tenantId, token).ConfigureAwait(false);
            if (ctx == null) return false;

            // Emit retry event (manual/operator retry)
            await EmitEventAsync("mission.landing_retry", "Retrying landing: " + ctx.Mission.Title, ctx.Mission, null, token).ConfigureAwait(false);

            try
            {
                await _Git.FetchAsync(ctx.RepoPath, token).ConfigureAwait(false);

                bool branchExists = await _Git.BranchExistsAsync(ctx.RepoPath, ctx.MissionBranch, token).ConfigureAwait(false);
                if (!branchExists)
                {
                    _Logging.Warn(_Header + "branch " + ctx.MissionBranch + " no longer exists -- cannot retry landing for mission " + missionId);
                    return false;
                }

                // Rebase the mission branch onto the current target tip so drift does not block landing.
                RebaseOutcomeEnum rebase = await _Git.RebaseOntoAsync(ctx.RepoPath, ctx.MissionBranch, ctx.TargetBranch, token).ConfigureAwait(false);
                if (rebase == RebaseOutcomeEnum.Conflict)
                {
                    _Logging.Warn(_Header + "rebase conflict for mission " + missionId + " branch " + ctx.MissionBranch + " onto " + ctx.TargetBranch + " -- genuine conflict, cannot retry landing");
                    await EmitEventAsync("mission.landing_rebase_conflict", "Landing rebase conflict: " + ctx.Mission.Title, ctx.Mission, null, token).ConfigureAwait(false);
                    await MarkLandingFailedAsync(missionId, tenantId, token).ConfigureAwait(false);
                    return false;
                }
                if (rebase == RebaseOutcomeEnum.Error)
                {
                    _Logging.Warn(_Header + "rebase error for mission " + missionId + " branch " + ctx.MissionBranch + " onto " + ctx.TargetBranch + " -- cannot retry landing");
                    await MarkLandingFailedAsync(missionId, tenantId, token).ConfigureAwait(false);
                    return false;
                }

                return await InvokeLandingAsync(ctx, tenantId, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "landing retry failed for mission " + missionId + ": " + ex.Message);
                await MarkLandingFailedAsync(missionId, tenantId, token).ConfigureAwait(false);
                return false;
            }
        }

        /// <inheritdoc />
        public async Task<bool> AutoRetryLandingAsync(string missionId, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));

            // MaxLandingRetries == 0 disables auto-retry entirely.
            if (_Settings.MaxLandingRetries <= 0)
            {
                _Logging.Info(_Header + "auto-retry disabled (MaxLandingRetries=0) for mission " + missionId);
                return false;
            }

            LandingRetryContext? ctx = await ResolveContextAsync(missionId, tenantId, token).ConfigureAwait(false);
            if (ctx == null) return false;

            int attempts = _AutoRetryCounts.GetValueOrDefault(missionId, 0);
            if (attempts >= _Settings.MaxLandingRetries)
            {
                _Logging.Warn(_Header + "mission " + missionId + " exhausted " + attempts + " landing retries (max " + _Settings.MaxLandingRetries + ") -- leaving LandingFailed");
                await EmitEventAsync(
                    "mission.landing_retry_exhausted",
                    "Landing auto-retry exhausted after " + attempts + " attempts: " + ctx.Mission.Title,
                    ctx.Mission,
                    JsonSerializer.Serialize(new { attempts = attempts, max = _Settings.MaxLandingRetries }),
                    token).ConfigureAwait(false);
                _AutoRetryCounts.TryRemove(missionId, out _);
                await MarkLandingFailedAsync(missionId, tenantId, token).ConfigureAwait(false);
                return false;
            }

            try
            {
                await _Git.FetchAsync(ctx.RepoPath, token).ConfigureAwait(false);

                bool branchExists = await _Git.BranchExistsAsync(ctx.RepoPath, ctx.MissionBranch, token).ConfigureAwait(false);
                if (!branchExists)
                {
                    _Logging.Warn(_Header + "branch " + ctx.MissionBranch + " no longer exists -- cannot auto-retry landing for mission " + missionId);
                    return false;
                }

                // Classify the failure before consuming a retry: only target-branch drift (a clean
                // rebase) consumes an attempt; a genuine conflict is left for a human.
                RebaseOutcomeEnum rebase = await _Git.RebaseOntoAsync(ctx.RepoPath, ctx.MissionBranch, ctx.TargetBranch, token).ConfigureAwait(false);
                if (rebase == RebaseOutcomeEnum.Conflict)
                {
                    _Logging.Warn(_Header + "auto-retry rebase conflict for mission " + missionId + " branch " + ctx.MissionBranch + " onto " + ctx.TargetBranch + " -- genuine conflict, not consuming a retry");
                    await EmitEventAsync("mission.landing_rebase_conflict", "Landing rebase conflict: " + ctx.Mission.Title, ctx.Mission, null, token).ConfigureAwait(false);
                    await MarkLandingFailedAsync(missionId, tenantId, token).ConfigureAwait(false);
                    return false;
                }
                if (rebase == RebaseOutcomeEnum.Error)
                {
                    _Logging.Warn(_Header + "auto-retry rebase error for mission " + missionId + " branch " + ctx.MissionBranch + " onto " + ctx.TargetBranch + " -- cannot auto-retry landing");
                    await MarkLandingFailedAsync(missionId, tenantId, token).ConfigureAwait(false);
                    return false;
                }

                // Clean rebase: target-branch drift resolved. Consume a retry and re-attempt landing.
                int attempt = attempts + 1;
                _AutoRetryCounts[missionId] = attempt;
                _Logging.Info(_Header + "auto-retrying landing for mission " + missionId + " (attempt " + attempt + " of " + _Settings.MaxLandingRetries + ") branch " + ctx.MissionBranch);
                await EmitEventAsync(
                    "mission.landing_auto_retry",
                    "Landing auto-retry attempt " + attempt + ": " + ctx.Mission.Title,
                    ctx.Mission,
                    JsonSerializer.Serialize(new { attempt = attempt, max = _Settings.MaxLandingRetries }),
                    token).ConfigureAwait(false);
                await EmitEventAsync("mission.landing_rebase_clean", "Landing rebase clean: " + ctx.Mission.Title, ctx.Mission, null, token).ConfigureAwait(false);

                bool landed = await InvokeLandingAsync(ctx, tenantId, token).ConfigureAwait(false);
                if (landed)
                {
                    _AutoRetryCounts.TryRemove(missionId, out _);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "auto-retry landing failed for mission " + missionId + ": " + ex.Message);
                await MarkLandingFailedAsync(missionId, tenantId, token).ConfigureAwait(false);
                return false;
            }
        }

        #endregion

        #region Private-Methods

        private async Task<LandingRetryContext?> ResolveContextAsync(string missionId, string? tenantId, CancellationToken token)
        {
            Mission? mission = !String.IsNullOrEmpty(tenantId)
                ? await _Database.Missions.ReadAsync(tenantId, missionId, token).ConfigureAwait(false)
                : await _Database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
            if (mission == null)
            {
                _Logging.Warn(_Header + "mission " + missionId + " not found");
                return null;
            }

            if (mission.Status != MissionStatusEnum.LandingFailed && mission.Status != MissionStatusEnum.WorkProduced)
            {
                _Logging.Warn(_Header + "mission " + missionId + " is in status " + mission.Status + ", not LandingFailed or WorkProduced -- cannot retry");
                return null;
            }

            if (String.IsNullOrEmpty(mission.VesselId))
            {
                _Logging.Warn(_Header + "mission " + missionId + " has no vessel -- cannot retry landing");
                return null;
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
                return null;
            }

            if (String.IsNullOrEmpty(vessel.LocalPath))
            {
                _Logging.Warn(_Header + "vessel " + vessel.Id + " has no LocalPath -- cannot retry landing for mission " + missionId);
                return null;
            }

            if (String.IsNullOrEmpty(mission.BranchName))
            {
                _Logging.Warn(_Header + "mission " + missionId + " has no branch name -- cannot retry landing");
                return null;
            }

            return new LandingRetryContext(mission, vessel, vessel.LocalPath, vessel.DefaultBranch, mission.BranchName);
        }

        private async Task<bool> InvokeLandingAsync(LandingRetryContext ctx, string? tenantId, CancellationToken token)
        {
            _Logging.Info(_Header + "retrying landing for mission " + ctx.Mission.Id + " branch " + ctx.MissionBranch);

            // Transition back to WorkProduced for landing attempt
            ctx.Mission.Status = MissionStatusEnum.WorkProduced;
            ctx.Mission.LastUpdateUtc = DateTime.UtcNow;
            await _Database.Missions.UpdateAsync(ctx.Mission, token).ConfigureAwait(false);

            // Try to find the dock or create a temporary context for landing
            Dock? dock = null;
            if (!String.IsNullOrEmpty(ctx.Mission.DockId))
            {
                dock = !String.IsNullOrEmpty(tenantId)
                    ? await _Database.Docks.ReadAsync(tenantId, ctx.Mission.DockId, token).ConfigureAwait(false)
                    : await _Database.Docks.ReadAsync(ctx.Mission.DockId, token).ConfigureAwait(false);
            }

            // If no dock, create a minimal one for the landing handler
            if (dock == null)
            {
                dock = new Dock(ctx.Vessel.Id);
                dock.BranchName = ctx.MissionBranch;
                dock.WorktreePath = ctx.Vessel.WorkingDirectory ?? ctx.Vessel.LocalPath;
                dock.Active = false; // Not a real provisioned dock
            }

            if (OnPerformLanding == null)
            {
                _Logging.Warn(_Header + "no landing handler configured -- cannot retry landing for mission " + ctx.Mission.Id);
                await MarkLandingFailedAsync(ctx.Mission.Id, tenantId, token).ConfigureAwait(false);
                return false;
            }

            await OnPerformLanding.Invoke(ctx.Mission, dock).ConfigureAwait(false);
            _Logging.Info(_Header + "landing retry completed for mission " + ctx.Mission.Id);

            // Re-read mission to get updated status from landing handler
            Mission? updated = !String.IsNullOrEmpty(tenantId)
                ? await _Database.Missions.ReadAsync(tenantId, ctx.Mission.Id, token).ConfigureAwait(false)
                : await _Database.Missions.ReadAsync(ctx.Mission.Id, token).ConfigureAwait(false);
            return updated != null && updated.Status == MissionStatusEnum.Complete;
        }

        private async Task MarkLandingFailedAsync(string missionId, string? tenantId, CancellationToken token)
        {
            try
            {
                Mission? mission = !String.IsNullOrEmpty(tenantId)
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
        }

        private async Task EmitEventAsync(string eventType, string message, Mission mission, string? payload, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent(eventType, message);
                evt.EntityType = "mission";
                evt.EntityId = mission.Id;
                evt.MissionId = mission.Id;
                evt.VesselId = mission.VesselId;
                evt.VoyageId = mission.VoyageId;
                if (!String.IsNullOrEmpty(payload)) evt.Payload = payload;
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch { }
        }

        #endregion

        #region Private-Types

        private sealed class LandingRetryContext
        {
            internal Mission Mission { get; }
            internal Vessel Vessel { get; }
            internal string RepoPath { get; }
            internal string TargetBranch { get; }
            internal string MissionBranch { get; }

            internal LandingRetryContext(Mission mission, Vessel vessel, string repoPath, string targetBranch, string missionBranch)
            {
                Mission = mission;
                Vessel = vessel;
                RepoPath = repoPath;
                TargetBranch = targetBranch;
                MissionBranch = missionBranch;
            }
        }

        #endregion
    }
}
