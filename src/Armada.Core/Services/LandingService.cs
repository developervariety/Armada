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
        private ConcurrentDictionary<string, int> _AutoRetryCounts = new ConcurrentDictionary<string, int>();

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

            LandingRetryContext? ctx = await ResolveRetryContextAsync(missionId, tenantId, token).ConfigureAwait(false);
            if (ctx == null) return false;

            await EmitEventAsync("mission.landing_retry", "Retrying landing: " + ctx.Mission.Title, ctx.Mission, null, token).ConfigureAwait(false);

            RebaseOutcomeEnum rebase = await PrepareBranchForRetryAsync(ctx, false, token).ConfigureAwait(false);
            if (rebase != RebaseOutcomeEnum.Clean)
            {
                await MarkLandingFailedAsync(ctx.Mission.Id, ctx.LookupTenantId, token).ConfigureAwait(false);
                return false;
            }

            await EmitEventAsync(
                "mission.landing_rebase_clean",
                "Landing rebase clean: " + ctx.Mission.Title,
                ctx.Mission,
                JsonSerializer.Serialize(new { branch = ctx.MissionBranch, targetBranch = ctx.TargetBranch }),
                token).ConfigureAwait(false);

            bool success = await PerformLandingRetryAsync(ctx, token).ConfigureAwait(false);
            if (success) _AutoRetryCounts.TryRemove(missionId, out _);
            return success;
        }

        /// <inheritdoc />
        public async Task<bool> AutoRetryLandingAsync(string missionId, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));

            if (_Settings.MaxLandingRetries <= 0)
            {
                _Logging.Info(_Header + "auto-retry disabled (MaxLandingRetries=0) for mission " + missionId);
                return false;
            }

            LandingRetryContext? ctx = await ResolveRetryContextAsync(missionId, tenantId, token).ConfigureAwait(false);
            if (ctx == null) return false;

            int attempts = _AutoRetryCounts.GetOrAdd(missionId, 0);
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
                return false;
            }

            RebaseOutcomeEnum rebase = await PrepareBranchForRetryAsync(ctx, true, token).ConfigureAwait(false);
            if (rebase != RebaseOutcomeEnum.Clean)
            {
                return false;
            }

            int attempt = _AutoRetryCounts.AddOrUpdate(missionId, 1, (key, current) => current + 1);
            _Logging.Info(_Header + "auto-retrying landing for mission " + missionId + " (attempt " + attempt + " of " + _Settings.MaxLandingRetries + ") branch " + ctx.MissionBranch);

            await EmitEventAsync(
                "mission.landing_auto_retry",
                "Landing auto-retry attempt " + attempt + ": " + ctx.Mission.Title,
                ctx.Mission,
                JsonSerializer.Serialize(new { attempt = attempt, max = _Settings.MaxLandingRetries }),
                token).ConfigureAwait(false);

            await EmitEventAsync(
                "mission.landing_rebase_clean",
                "Landing rebase clean: " + ctx.Mission.Title,
                ctx.Mission,
                JsonSerializer.Serialize(new { attempt = attempt, max = _Settings.MaxLandingRetries, branch = ctx.MissionBranch, targetBranch = ctx.TargetBranch }),
                token).ConfigureAwait(false);

            bool success = await PerformLandingRetryAsync(ctx, token).ConfigureAwait(false);
            if (success) _AutoRetryCounts.TryRemove(missionId, out _);
            return success;
        }

        #endregion

        #region Private-Methods

        private async Task<RebaseOutcomeEnum> PrepareBranchForRetryAsync(LandingRetryContext ctx, bool automatic, CancellationToken token)
        {
            try
            {
                await _Git.FetchAsync(ctx.RepoPath, token).ConfigureAwait(false);

                bool branchExists = await _Git.BranchExistsAsync(ctx.RepoPath, ctx.MissionBranch, token).ConfigureAwait(false);
                if (!branchExists)
                {
                    _Logging.Warn(_Header + "branch " + ctx.MissionBranch + " no longer exists -- cannot retry landing for mission " + ctx.Mission.Id);
                    return RebaseOutcomeEnum.Error;
                }

                RebaseOutcomeEnum rebase = await _Git.RebaseOntoAsync(ctx.RepoPath, ctx.MissionBranch, ctx.TargetBranch, token).ConfigureAwait(false);
                if (rebase == RebaseOutcomeEnum.Conflict)
                {
                    string prefix = automatic ? "auto-retry " : "";
                    _Logging.Warn(_Header + prefix + "rebase conflict for mission " + ctx.Mission.Id + " branch " + ctx.MissionBranch + " onto " + ctx.TargetBranch + " -- genuine conflict, not consuming an auto-retry");
                    await EmitEventAsync(
                        "mission.landing_rebase_conflict",
                        "Landing rebase conflict: " + ctx.Mission.Title,
                        ctx.Mission,
                        JsonSerializer.Serialize(new { branch = ctx.MissionBranch, targetBranch = ctx.TargetBranch }),
                        token).ConfigureAwait(false);
                }
                else if (rebase == RebaseOutcomeEnum.Error)
                {
                    string prefix = automatic ? "auto-retry " : "";
                    _Logging.Warn(_Header + prefix + "rebase error for mission " + ctx.Mission.Id + " branch " + ctx.MissionBranch + " onto " + ctx.TargetBranch + " -- cannot retry landing");
                }

                return rebase;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "landing retry preparation failed for mission " + ctx.Mission.Id + ": " + ex.Message);
                return RebaseOutcomeEnum.Error;
            }
        }

        private async Task<LandingRetryContext?> ResolveRetryContextAsync(string missionId, string? tenantId, CancellationToken token)
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

            string? lookupTenantId = !String.IsNullOrEmpty(mission.TenantId) ? mission.TenantId : tenantId;
            Vessel? vessel = !String.IsNullOrEmpty(lookupTenantId)
                ? await _Database.Vessels.ReadAsync(lookupTenantId, mission.VesselId, token).ConfigureAwait(false)
                : await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
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

            return new LandingRetryContext(mission, vessel, vessel.LocalPath, vessel.DefaultBranch, mission.BranchName, lookupTenantId);
        }

        private async Task<bool> PerformLandingRetryAsync(LandingRetryContext ctx, CancellationToken token)
        {
            try
            {
                _Logging.Info(_Header + "retrying landing for mission " + ctx.Mission.Id + " branch " + ctx.MissionBranch);

                ctx.Mission.Status = MissionStatusEnum.WorkProduced;
                ctx.Mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(ctx.Mission, token).ConfigureAwait(false);

                Dock? dock = null;
                if (!String.IsNullOrEmpty(ctx.Mission.DockId))
                {
                    dock = !String.IsNullOrEmpty(ctx.LookupTenantId)
                        ? await _Database.Docks.ReadAsync(ctx.LookupTenantId, ctx.Mission.DockId, token).ConfigureAwait(false)
                        : await _Database.Docks.ReadAsync(ctx.Mission.DockId, token).ConfigureAwait(false);
                }

                if (dock == null)
                {
                    dock = new Dock(ctx.Vessel.Id);
                    dock.BranchName = ctx.MissionBranch;
                    dock.WorktreePath = ctx.Vessel.WorkingDirectory ?? ctx.Vessel.LocalPath;
                    dock.Active = false;
                }

                if (OnPerformLanding == null)
                {
                    _Logging.Warn(_Header + "no landing handler configured -- cannot retry landing for mission " + ctx.Mission.Id);
                    await MarkLandingFailedAsync(ctx.Mission.Id, ctx.LookupTenantId, token).ConfigureAwait(false);
                    return false;
                }

                await OnPerformLanding.Invoke(ctx.Mission, dock).ConfigureAwait(false);
                _Logging.Info(_Header + "landing retry completed for mission " + ctx.Mission.Id);

                Mission? updated = !String.IsNullOrEmpty(ctx.LookupTenantId)
                    ? await _Database.Missions.ReadAsync(ctx.LookupTenantId, ctx.Mission.Id, token).ConfigureAwait(false)
                    : await _Database.Missions.ReadAsync(ctx.Mission.Id, token).ConfigureAwait(false);
                return updated != null && updated.Status == MissionStatusEnum.Complete;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "landing retry failed for mission " + ctx.Mission.Id + ": " + ex.Message);
                await MarkLandingFailedAsync(ctx.Mission.Id, ctx.LookupTenantId, token).ConfigureAwait(false);
                return false;
            }
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
                evt.CaptainId = mission.CaptainId;
                evt.Payload = payload;
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch { }
        }

        #endregion

        #region Private-Classes

        private sealed class LandingRetryContext
        {
            internal Mission Mission { get; }
            internal Vessel Vessel { get; }
            internal string RepoPath { get; }
            internal string TargetBranch { get; }
            internal string MissionBranch { get; }
            internal string? LookupTenantId { get; }

            internal LandingRetryContext(Mission mission, Vessel vessel, string repoPath, string targetBranch, string missionBranch, string? lookupTenantId)
            {
                Mission = mission;
                Vessel = vessel;
                RepoPath = repoPath;
                TargetBranch = targetBranch;
                MissionBranch = missionBranch;
                LookupTenantId = lookupTenantId;
            }
        }

        #endregion
    }
}
