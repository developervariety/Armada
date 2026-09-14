namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Server.WebSocket;
    using SyslogLogging;

    /// <summary>
    /// The one operator path for mission status transitions. REST, WebSocket, and MCP entry points
    /// all call it, so a manual Complete meets the same gates wherever it is requested.
    /// </summary>
    /// <remarks>
    /// A Complete request first proves captain-process release and the immutable completion
    /// conditions (review and Judge authority, Checks, and target ancestry for work no active dock
    /// will land). With an active dock the mission is routed through the landing pipeline; an
    /// intermediate pipeline stage is handed off through the shared completion service instead of
    /// being marked terminal. A refusal never mutates the mission and always carries its reason.
    /// </remarks>
    public sealed class MissionStatusTransitionService
    {
        /// <summary>
        /// Error an entry point returns for a status transition when the server did not supply this
        /// service. Transitions are refused rather than applied without their gates.
        /// </summary>
        public const string UnavailableMessage =
            "Mission status transitions are unavailable: the shared transition service is not configured.";

        private const string _Header = "[MissionStatusTransition] ";

        private readonly DatabaseDriver _Database;
        private readonly IAdmiralService _Admiral;
        private readonly IMissionService _MissionService;
        private readonly ManualCompletionProofService _ManualCompletionProof;
        private readonly Func<Mission, CancellationToken, Task<bool>> _IsMissionProcessActive;
        private readonly Func<Mission, Dock, Task> _HandleMissionComplete;
        private readonly Func<string, string, string?, string?, string?, string?, string?, string?, Task> _EmitEvent;
        private readonly LoggingModule _Logging;
        private ArmadaWebSocketHub? _WebSocketHub;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="admiral">Admiral service providing the diff and landing callback seams.</param>
        /// <param name="missionService">Mission service used for intermediate pipeline handoff.</param>
        /// <param name="git">Git service used by the ancestry proof.</param>
        /// <param name="isMissionProcessActive">Authoritative captain process ownership probe.</param>
        /// <param name="handleMissionComplete">Landing handler used when the Admiral seam is unset.</param>
        /// <param name="emitEvent">Event broadcast callback.</param>
        /// <param name="logging">Logging module.</param>
        public MissionStatusTransitionService(
            DatabaseDriver database,
            IAdmiralService admiral,
            IMissionService missionService,
            IGitService git,
            Func<Mission, CancellationToken, Task<bool>> isMissionProcessActive,
            Func<Mission, Dock, Task> handleMissionComplete,
            Func<string, string, string?, string?, string?, string?, string?, string?, Task> emitEvent,
            LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Admiral = admiral ?? throw new ArgumentNullException(nameof(admiral));
            _MissionService = missionService ?? throw new ArgumentNullException(nameof(missionService));
            _ManualCompletionProof = new ManualCompletionProofService(database, git ?? throw new ArgumentNullException(nameof(git)));
            _IsMissionProcessActive = isMissionProcessActive ?? throw new ArgumentNullException(nameof(isMissionProcessActive));
            _HandleMissionComplete = handleMissionComplete ?? throw new ArgumentNullException(nameof(handleMissionComplete));
            _EmitEvent = emitEvent ?? throw new ArgumentNullException(nameof(emitEvent));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <summary>
        /// Set the WebSocket hub used for mission change notifications. The hub is created after
        /// this service because its command handler depends on it.
        /// </summary>
        public void SetWebSocketHub(ArmadaWebSocketHub? hub)
        {
            _WebSocketHub = hub;
        }

        /// <summary>
        /// Apply an operator status transition.
        /// </summary>
        /// <param name="mission">Mission already read under the caller's authorization scope.</param>
        /// <param name="newStatus">Requested status.</param>
        /// <param name="readDockAsync">Scoped dock reader; null reads without tenant scope.</param>
        /// <param name="readCaptainAsync">Scoped captain reader; null reads without tenant scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The outcome, with the resulting mission or the refusal reason.</returns>
        public async Task<MissionStatusTransitionResult> TransitionAsync(
            Mission mission,
            MissionStatusEnum newStatus,
            Func<string, Task<Dock?>>? readDockAsync = null,
            Func<string, Task<Captain?>>? readCaptainAsync = null,
            CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            Func<string, Task<Dock?>> readDock = readDockAsync ?? (dockId => _Database.Docks.ReadAsync(dockId, token));
            Func<string, Task<Captain?>> readCaptain = readCaptainAsync ?? (captainId => _Database.Captains.ReadAsync(captainId, token));
            string id = mission.Id;

            if (!MissionStateMachine.IsValidTransition(mission.Status, newStatus))
                return MissionStatusTransitionResult.InvalidTransition(mission, "Invalid transition from " + mission.Status + " to " + newStatus);

            if (newStatus == MissionStatusEnum.Complete && !String.IsNullOrEmpty(mission.DockId))
            {
                Dock? landingDock = await readDock(mission.DockId).ConfigureAwait(false);
                if (landingDock != null && landingDock.Active)
                {
                    ManualCompletionProofResult preflight = await EvaluateManualCompletionAsync(mission, true, token).ConfigureAwait(false);
                    if (!preflight.Allowed)
                        return MissionStatusTransitionResult.Refused(mission, preflight.Reason);

                    bool handedOff = await HasDependentPipelineStageAsync(mission, token).ConfigureAwait(false);
                    if (handedOff)
                    {
                        Captain? completionCaptain = await ReadCompletionCaptainAsync(mission, readCaptain).ConfigureAwait(false);
                        if (completionCaptain == null)
                            return MissionStatusTransitionResult.Refused(mission, "manual_completion_captain_unavailable");

                        // Intermediate stages use the same shared completion service as an agent
                        // exit. It captures the diff, prepares the downstream stage, and does not
                        // call the landing handler while a dependent stage remains.
                        await _MissionService.HandleCompletionAsync(completionCaptain, id).ConfigureAwait(false);
                        mission = await _Database.Missions.ReadAsync(id, token).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Mission disappeared during manual pipeline handoff.");
                    }
                    else
                    {
                        if (_Admiral.OnCaptureDiff != null)
                        {
                            try
                            {
                                await _Admiral.OnCaptureDiff.Invoke(mission, landingDock).ConfigureAwait(false);
                            }
                            catch (Exception diffEx)
                            {
                                _Logging.Warn(_Header + "error capturing diff during manual completion of " + id + ": " + diffEx.Message);
                            }
                        }

                        // The landing handler processes WorkProduced missions.
                        mission.Status = MissionStatusEnum.WorkProduced;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);

                        _Logging.Info(_Header + "manual Complete transition for " + id + " routing through landing pipeline");

                        // The Admiral seam lets agent completion and manual completion share one
                        // landing handler, and lets isolated tests observe that it is not reached
                        // for an intermediate handoff.
                        Func<Mission, Dock, Task> completionHandler = _Admiral.OnMissionComplete ?? _HandleMissionComplete;
                        await completionHandler(mission, landingDock).ConfigureAwait(false);

                        // The immutable proof ran before capture and landing, so a post-landing
                        // downgrade cannot undo a merge that was allowed without that proof.
                        Mission? landed = await _Database.Missions.ReadAsync(id, token).ConfigureAwait(false);
                        if (landed == null) return MissionStatusTransitionResult.MissionMissing();
                        mission = landed;
                    }

                    string outcomeText = handedOff ? "handed off as " : "landed as ";
                    Signal landingSignal = new Signal(SignalTypeEnum.Progress, "Mission " + id + " manual completion — " + outcomeText + mission.Status);
                    if (!String.IsNullOrEmpty(mission.CaptainId)) landingSignal.FromCaptainId = mission.CaptainId;
                    await _Database.Signals.CreateAsync(landingSignal, token).ConfigureAwait(false);

                    await _EmitEvent("mission.status_changed", "Mission " + id + " manually completed — " + outcomeText + mission.Status,
                        "mission", id, mission.CaptainId, id, mission.VesselId, mission.VoyageId).ConfigureAwait(false);

                    // The record overload carries the mission owner's delivery scope.
                    _WebSocketHub?.BroadcastMissionChange(mission);
                    return MissionStatusTransitionResult.Applied(mission);
                }
            }

            bool intermediateCompletionHandled = false;
            if (newStatus == MissionStatusEnum.Complete)
            {
                ManualCompletionProofResult proof = await EvaluateManualCompletionAsync(mission, false, token).ConfigureAwait(false);
                if (!proof.Allowed)
                    return MissionStatusTransitionResult.Refused(mission, proof.Reason);

                if (await HasDependentPipelineStageAsync(mission, token).ConfigureAwait(false))
                {
                    Captain? completionCaptain = await ReadCompletionCaptainAsync(mission, readCaptain).ConfigureAwait(false);
                    if (completionCaptain == null)
                        return MissionStatusTransitionResult.Refused(mission, "manual_completion_captain_unavailable");

                    await _MissionService.HandleCompletionAsync(completionCaptain, id).ConfigureAwait(false);
                    mission = await _Database.Missions.ReadAsync(id, token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Mission disappeared during manual pipeline handoff.");
                    intermediateCompletionHandled = true;
                    // Report the durable handoff result. The requested Complete status was only the
                    // operator trigger; it is not the persisted outcome for an intermediate stage.
                    newStatus = mission.Status;
                }
            }

            if (!intermediateCompletionHandled)
            {
                mission.Status = newStatus;
                mission.LastUpdateUtc = DateTime.UtcNow;

                if (newStatus == MissionStatusEnum.InProgress && mission.StartedUtc == null)
                    mission.StartedUtc = DateTime.UtcNow;

                if (newStatus == MissionStatusEnum.Complete || newStatus == MissionStatusEnum.Failed
                    || newStatus == MissionStatusEnum.LandingFailed || newStatus == MissionStatusEnum.Cancelled)
                {
                    mission.CompletedUtc = DateTime.UtcNow;
                }

                mission = await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            }

            Signal signal = new Signal(SignalTypeEnum.Progress, "Mission " + id + " transitioned to " + newStatus);
            if (!String.IsNullOrEmpty(mission.CaptainId)) signal.FromCaptainId = mission.CaptainId;
            await _Database.Signals.CreateAsync(signal, token).ConfigureAwait(false);

            await _EmitEvent("mission.status_changed", "Mission " + id + " transitioned to " + newStatus,
                "mission", id, mission.CaptainId, id, mission.VesselId, mission.VoyageId).ConfigureAwait(false);

            if (_WebSocketHub != null)
            {
                _WebSocketHub.BroadcastMissionChange(mission);
                if (newStatus == MissionStatusEnum.Review)
                    _WebSocketHub.BroadcastApprovalNeeded(mission);
            }

            return MissionStatusTransitionResult.Applied(mission);
        }

        private async Task<ManualCompletionProofResult> EvaluateManualCompletionAsync(
            Mission mission,
            bool activeLandingPipeline,
            CancellationToken token)
        {
            bool processActive;
            try
            {
                processActive = await _IsMissionProcessActive(mission, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "process liveness for mission " + mission.Id + " is unknown: " + ex.Message);
                return ManualCompletionProofResult.Fail("manual_completion_process_liveness_unknown");
            }
            if (processActive)
                return ManualCompletionProofResult.Fail("manual_completion_process_active");

            return await _ManualCompletionProof.EvaluateAsync(mission, activeLandingPipeline, token).ConfigureAwait(false);
        }

        private async Task<bool> HasDependentPipelineStageAsync(Mission mission, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(mission.VoyageId)) return false;
            List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(mission.VoyageId, token).ConfigureAwait(false);
            return voyageMissions.Any(candidate => String.Equals(candidate.DependsOnMissionId, mission.Id, StringComparison.Ordinal));
        }

        private static async Task<Captain?> ReadCompletionCaptainAsync(Mission mission, Func<string, Task<Captain?>> readCaptain)
        {
            if (String.IsNullOrWhiteSpace(mission.CaptainId)) return null;
            Captain? captain = await readCaptain(mission.CaptainId).ConfigureAwait(false);
            if (captain == null || !String.Equals(captain.CurrentMissionId, mission.Id, StringComparison.Ordinal)) return null;
            return captain;
        }
    }
}
