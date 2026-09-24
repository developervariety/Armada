namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The operator operations on missions and voyages that REST, WebSocket and MCP all expose. Each operation owns its
    /// rule, its state change, its event and its broadcast; a surface reads the record under its caller's scope, calls
    /// the operation, and only turns the result into its own response shape.
    /// </summary>
    public sealed class MissionOperations
    {
        #region Public-Members

        /// <summary>
        /// Where the operations report what they changed.
        /// </summary>
        public OperationNotifier Notifier { get; }

        #endregion

        #region Private-Members

        private const string _Header = "[MissionOperations] ";

        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;
        private readonly IDockService? _Docks;
        private readonly Func<string, CancellationToken, Task>? _RecallCaptain;
        private readonly LoggingModule? _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Server settings (log directory, capacity limits).</param>
        /// <param name="docks">Dock service that removes a dock record and its worktree under its ownership guard.</param>
        /// <param name="recallCaptain">Captain recall, which stops the captain's agent process and releases it.</param>
        /// <param name="notifier">Event and broadcast sink.</param>
        /// <param name="logging">Optional logging module.</param>
        public MissionOperations(
            DatabaseDriver database,
            ArmadaSettings settings,
            IDockService? docks,
            Func<string, CancellationToken, Task>? recallCaptain,
            OperationNotifier? notifier,
            LoggingModule? logging = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Docks = docks;
            _RecallCaptain = recallCaptain;
            Notifier = notifier ?? OperationNotifier.None;
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Cancel one mission through <see cref="MissionCancellation"/>, then write a <c>mission.cancelled</c> event
        /// and broadcast the change, and do the same with <c>mission.cancelled_dependency</c> for each waiting stage
        /// cancelled with it.
        /// </summary>
        /// <param name="mission">Mission, already read under the caller's scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>What changed, or the refusal.</returns>
        public async Task<MissionCancellationResult> CancelMissionAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            MissionCancellationResult result = await MissionCancellation.CancelAsync(
                _Database, mission, MissionCancellation.OperatorCancelReason, _RecallCaptain, token).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _Logging?.Info(_Header + "cancel of mission " + mission.Id + " refused (" + result.Code + "): " + result.Message);
                return result;
            }

            Mission cancelled = result.Mission;
            await Notifier.EmitAsync("mission.cancelled", "Mission " + cancelled.Id + " cancelled by operator",
                "mission", cancelled.Id, result.RecalledCaptainId, cancelled.Id, cancelled.VesselId, cancelled.VoyageId).ConfigureAwait(false);
            Notifier.MissionChanged(cancelled);

            foreach (Mission dependent in result.CancelledDependents)
            {
                await Notifier.EmitAsync("mission.cancelled_dependency",
                    "Mission cancelled: blocked by cancelled dependency " + cancelled.Id,
                    "mission", dependent.Id, null, dependent.Id, dependent.VesselId, dependent.VoyageId).ConfigureAwait(false);
                Notifier.MissionChanged(dependent);
            }

            return result;
        }

        /// <summary>
        /// Permanently delete one mission and its resources, then write a <c>mission.deleted</c> event.
        /// <para>
        /// A mission a captain is working (Assigned, InProgress, Testing, Review, or still held by its captain) is
        /// refused and keeps its worktree. Otherwise the mission's dock record and worktree are removed through the
        /// dock service, which leaves a worktree another active dock now owns; its log files and saved diff are
        /// deleted; and the mission row is deleted.
        /// </para>
        /// </summary>
        /// <param name="mission">Mission, already read under the caller's scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>What was deleted, or the refusal.</returns>
        public async Task<WorkPurgeResult> PurgeMissionAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            WorkPurgeResult? refusal = await FindMissionPurgeRefusalAsync(mission, token).ConfigureAwait(false);
            if (refusal != null) return refusal;

            await DeleteMissionAsync(mission, token).ConfigureAwait(false);
            await Notifier.EmitAsync("mission.deleted", "Mission " + mission.Id + " permanently deleted",
                "mission", mission.Id, null, null, null, null).ConfigureAwait(false);
            return WorkPurgeResult.Deleted(mission.Id, 1);
        }

        /// <summary>
        /// Permanently delete several missions by the rule of <see cref="PurgeMissionAsync"/>, skipping each one it
        /// refuses, then write one <c>mission.batch_deleted</c> event.
        /// </summary>
        /// <param name="ids">Requested mission identifiers.</param>
        /// <param name="readInScope">The surface's caller-scoped mission read; a mission outside the scope reads as absent.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Deleted count and skipped entries with their reasons.</returns>
        public async Task<DeleteMultipleResult> PurgeMissionsAsync(
            IEnumerable<string?> ids,
            Func<string, Task<Mission?>> readInScope,
            CancellationToken token = default)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            if (readInScope == null) throw new ArgumentNullException(nameof(readInScope));

            DeleteMultipleResult result = new DeleteMultipleResult();
            foreach (string? id in ids)
            {
                if (String.IsNullOrEmpty(id))
                {
                    result.Skipped.Add(new DeleteMultipleSkipped(id ?? "", "Empty ID"));
                    continue;
                }
                Mission? mission = await readInScope(id).ConfigureAwait(false);
                if (mission == null)
                {
                    result.Skipped.Add(new DeleteMultipleSkipped(id, "Not found"));
                    continue;
                }
                WorkPurgeResult? refusal = await FindMissionPurgeRefusalAsync(mission, token).ConfigureAwait(false);
                if (refusal != null)
                {
                    result.Skipped.Add(new DeleteMultipleSkipped(id, refusal.Message ?? "Refused"));
                    continue;
                }
                await DeleteMissionAsync(mission, token).ConfigureAwait(false);
                result.Deleted++;
            }

            await Notifier.EmitAsync("mission.batch_deleted", "Batch deleted " + result.Deleted + " missions",
                "mission", null, null, null, null, null).ConfigureAwait(false);
            result.ResolveStatus();
            return result;
        }

        /// <summary>
        /// Permanently delete a voyage and every mission in it, then write a <c>voyage.deleted</c> event. A live voyage
        /// (Open or InProgress) is refused, and so is a voyage holding a mission a captain is working; otherwise each
        /// mission is deleted with its resources by the rule of <see cref="PurgeMissionAsync"/>.
        /// </summary>
        /// <param name="voyage">Voyage, already read under the caller's scope.</param>
        /// <param name="enumerateMissions">The surface's caller-scoped read of the voyage's missions; null reads them all.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>What was deleted, or the refusal.</returns>
        public async Task<WorkPurgeResult> PurgeVoyageAsync(
            Voyage voyage,
            Func<string, Task<List<Mission>>>? enumerateMissions = null,
            CancellationToken token = default)
        {
            if (voyage == null) throw new ArgumentNullException(nameof(voyage));
            VoyagePurgePlan plan = await PlanVoyagePurgeAsync(voyage, enumerateMissions, token).ConfigureAwait(false);
            if (plan.Refusal != null) return plan.Refusal;

            await DeleteVoyageAsync(voyage, plan.Missions, token).ConfigureAwait(false);
            await Notifier.EmitAsync("voyage.deleted", "Voyage " + voyage.Id + " permanently deleted with " + plan.Missions.Count + " missions",
                "voyage", voyage.Id, null, null, null, null).ConfigureAwait(false);
            return WorkPurgeResult.Deleted(voyage.Id, plan.Missions.Count);
        }

        /// <summary>
        /// Permanently delete several voyages by the rule of <see cref="PurgeVoyageAsync"/>, skipping each one it
        /// refuses, then write one <c>voyage.batch_deleted</c> event.
        /// </summary>
        /// <param name="ids">Requested voyage identifiers.</param>
        /// <param name="readInScope">The surface's caller-scoped voyage read.</param>
        /// <param name="enumerateMissions">The surface's caller-scoped read of a voyage's missions; null reads them all.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Deleted count and skipped entries with their reasons.</returns>
        public async Task<DeleteMultipleResult> PurgeVoyagesAsync(
            IEnumerable<string?> ids,
            Func<string, Task<Voyage?>> readInScope,
            Func<string, Task<List<Mission>>>? enumerateMissions = null,
            CancellationToken token = default)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            if (readInScope == null) throw new ArgumentNullException(nameof(readInScope));

            DeleteMultipleResult result = new DeleteMultipleResult();
            foreach (string? id in ids)
            {
                if (String.IsNullOrEmpty(id))
                {
                    result.Skipped.Add(new DeleteMultipleSkipped(id ?? "", "Empty ID"));
                    continue;
                }
                Voyage? voyage = await readInScope(id).ConfigureAwait(false);
                if (voyage == null)
                {
                    result.Skipped.Add(new DeleteMultipleSkipped(id, "Not found"));
                    continue;
                }
                VoyagePurgePlan plan = await PlanVoyagePurgeAsync(voyage, enumerateMissions, token).ConfigureAwait(false);
                if (plan.Refusal != null)
                {
                    result.Skipped.Add(new DeleteMultipleSkipped(id, plan.Refusal.Message ?? "Refused"));
                    continue;
                }
                await DeleteVoyageAsync(voyage, plan.Missions, token).ConfigureAwait(false);
                result.Deleted++;
            }

            await Notifier.EmitAsync("voyage.batch_deleted", "Batch deleted " + result.Deleted + " voyages",
                "voyage", null, null, null, null, null).ConfigureAwait(false);
            result.ResolveStatus();
            return result;
        }

        /// <summary>
        /// Restart one mission: return a Failed or Cancelled mission to Pending through
        /// <see cref="MissionRestartService"/>, which owns the eligibility rule and the capacity gate, then record a
        /// restart signal owned by the mission's owner, write a <c>mission.restarted</c> event, and broadcast the
        /// change. A LandingFailed mission is refused with a pointer to retry-landing.
        /// </summary>
        /// <param name="mission">Mission, already read under the caller's scope.</param>
        /// <param name="title">Optional new title; blank keeps the current one.</param>
        /// <param name="description">Optional new description; blank keeps the current one.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The restarted mission, or the refusal.</returns>
        /// <exception cref="FleetCapacityAdmissionException">The fleet or vessel has no capacity for the restarted work.</exception>
        public async Task<MissionRestartResult> RestartMissionAsync(Mission mission, string? title, string? description, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            string? ineligible = MissionRestartService.FindIneligibility(mission, out string? code);
            if (ineligible != null) return MissionRestartResult.Refused(mission, code ?? MissionRestartService.NotRestartableCode, ineligible);

            Mission restarted;
            try
            {
                restarted = await new MissionRestartService(_Database, _Settings, _Logging)
                    .RestartAsync(mission, title, description, token).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex is not FleetCapacityAdmissionException)
            {
                return MissionRestartResult.Refused(mission, MissionRestartService.NotRestartableCode, ex.Message);
            }

            // The restart signal belongs to the mission it reports, so the mission's owner sees it.
            Signal signal = new Signal(SignalTypeEnum.Progress, "Mission " + restarted.Id + " restarted");
            signal.TenantId = restarted.TenantId;
            signal.UserId = restarted.UserId;
            await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

            await Notifier.EmitAsync("mission.restarted", "Mission " + restarted.Id + " restarted",
                "mission", restarted.Id, null, restarted.Id, restarted.VesselId, restarted.VoyageId).ConfigureAwait(false);
            Notifier.MissionChanged(restarted);
            return MissionRestartResult.Restarted(restarted);
        }

        #endregion

        #region Private-Methods

        private async Task<WorkPurgeResult?> FindMissionPurgeRefusalAsync(Mission mission, CancellationToken token)
        {
            if (MissionStateMachine.IsActive(mission.Status))
            {
                return WorkPurgeResult.Refused(mission.Id, WorkPurgeResult.WorkActiveCode,
                    "Mission " + mission.Id + " is " + mission.Status + " and a captain is working it; cancel it before deleting it.");
            }

            if (!String.IsNullOrEmpty(mission.CaptainId))
            {
                Captain? captain = await _Database.Captains.ReadAsync(mission.CaptainId, token).ConfigureAwait(false);
                if (captain != null && String.Equals(captain.CurrentMissionId, mission.Id, StringComparison.Ordinal))
                {
                    return WorkPurgeResult.Refused(mission.Id, WorkPurgeResult.WorkActiveCode,
                        "Mission " + mission.Id + " is still held by captain " + captain.Id + "; stop or recall the captain before deleting it.");
                }
            }

            if (!String.IsNullOrEmpty(mission.DockId) && _Docks == null)
            {
                Dock? dock = await _Database.Docks.ReadAsync(mission.DockId, token).ConfigureAwait(false);
                if (dock != null)
                {
                    return WorkPurgeResult.Refused(mission.Id, WorkPurgeResult.DockUnavailableCode,
                        "Mission " + mission.Id + " has dock " + dock.Id + ", and no dock service is available to remove it.");
                }
            }

            return null;
        }

        private async Task<VoyagePurgePlan> PlanVoyagePurgeAsync(
            Voyage voyage,
            Func<string, Task<List<Mission>>>? enumerateMissions,
            CancellationToken token)
        {
            VoyagePurgePlan plan = new VoyagePurgePlan();
            if (ObjectiveService.IsActiveVoyageStatus(voyage.Status))
            {
                plan.Refusal = WorkPurgeResult.Refused(voyage.Id, WorkPurgeResult.WorkActiveCode,
                    "Cannot delete voyage while status is " + voyage.Status + ". Cancel the voyage first.");
                return plan;
            }

            plan.Missions = enumerateMissions != null
                ? await enumerateMissions(voyage.Id).ConfigureAwait(false)
                : await _Database.Missions.EnumerateByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
            List<string> refusals = new List<string>();
            foreach (Mission mission in plan.Missions)
            {
                WorkPurgeResult? refusal = await FindMissionPurgeRefusalAsync(mission, token).ConfigureAwait(false);
                if (refusal != null) refusals.Add(refusal.Message ?? mission.Id);
            }
            if (refusals.Count > 0)
            {
                plan.Refusal = WorkPurgeResult.Refused(voyage.Id, WorkPurgeResult.WorkActiveCode,
                    "Cannot delete voyage with " + refusals.Count + " mission(s) still at work or held: " + String.Join(" ", refusals));
            }
            return plan;
        }

        private async Task DeleteVoyageAsync(Voyage voyage, List<Mission> missions, CancellationToken token)
        {
            foreach (Mission mission in missions)
            {
                await DeleteMissionAsync(mission, token).ConfigureAwait(false);
            }
            await _Database.Voyages.DeleteAsync(voyage.Id, token).ConfigureAwait(false);
        }

        private async Task DeleteMissionAsync(Mission mission, CancellationToken token)
        {
            if (!String.IsNullOrEmpty(mission.DockId) && _Docks != null)
            {
                Dock? dock = await _Database.Docks.ReadAsync(mission.DockId, token).ConfigureAwait(false);
                if (dock != null)
                {
                    // The guarded delete removes the worktree and applies the vessel's branch policy. A dock still marked
                    // active for a captain that no longer holds this mission has no live writer, so it is purged instead.
                    bool deleted = await _Docks.DeleteAsync(dock.Id, null, token).ConfigureAwait(false);
                    if (!deleted) await _Docks.PurgeAsync(dock.Id, null, token).ConfigureAwait(false);
                }
            }

            DeleteMissionFiles(mission.Id);
            await _Database.Missions.DeleteAsync(mission.Id, token).ConfigureAwait(false);
        }

        private void DeleteMissionFiles(string missionId)
        {
            List<string> paths = new List<string>();
            string missionLogDir = Path.Combine(_Settings.LogDirectory, "missions");
            paths.Add(Path.Combine(missionLogDir, missionId + ".log"));
            if (Directory.Exists(missionLogDir))
                paths.AddRange(Directory.GetFiles(missionLogDir, missionId + ".*.log"));
            paths.Add(Path.Combine(_Settings.LogDirectory, "diffs", missionId + ".diff"));

            foreach (string path in paths.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _Logging?.Warn(_Header + "could not delete " + path + " of deleted mission " + missionId + "; the file remains on disk: " + ex.Message);
                }
            }
        }

        #endregion

        #region Private-Types

        private sealed class VoyagePurgePlan
        {
            public List<Mission> Missions { get; set; } = new List<Mission>();
            public WorkPurgeResult? Refusal { get; set; }
        }

        #endregion
    }
}
