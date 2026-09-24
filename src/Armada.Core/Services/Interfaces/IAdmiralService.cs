namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Top-level orchestration service for coordinating missions and captains.
    /// </summary>
    public interface IAdmiralService
    {
        /// <summary>
        /// The runtime dispatch hold this admiral enforces, or null when none is configured. Dispatch
        /// paths that create voyages outside the admiral check this same hold before creating anything.
        /// </summary>
        Armada.Core.Services.DispatchHold? DispatchHold => null;

        /// <summary>
        /// Delegate invoked when a captain needs an agent process started.
        /// The handler receives (captain, mission, dock) and should return the process ID.
        /// </summary>
        Func<Captain, Mission, Dock, Task<int>>? OnLaunchAgent { get; set; }

        /// <summary>
        /// Delegate invoked when a captain's agent process should be stopped.
        /// </summary>
        Func<Captain, Task>? OnStopAgent { get; set; }

        /// <summary>
        /// Delegate invoked synchronously at completion to capture the diff before the worktree can be reclaimed.
        /// </summary>
        Func<Mission, Dock, Task>? OnCaptureDiff { get; set; }

        /// <summary>
        /// Delegate invoked when a mission completes and branch should be pushed/PR created.
        /// The handler receives (mission, dock).
        /// </summary>
        Func<Mission, Dock, Task>? OnMissionComplete { get; set; }

        /// <summary>
        /// Delegate invoked when a voyage completes (all missions done).
        /// The handler receives the completed voyage.
        /// </summary>
        Func<Voyage, Task>? OnVoyageComplete { get; set; }

        /// <summary>
        /// Delegate invoked during health check to reconcile PullRequestOpen missions.
        /// The handler receives a mission and should check if its PR has been merged,
        /// returning true if the mission was reconciled (transitioned to Complete or LandingFailed).
        /// </summary>
        Func<Mission, Task<bool>>? OnReconcilePullRequest { get; set; }

        /// <summary>
        /// Delegate invoked during health check to reconcile PullRequestOpen merge
        /// entries (PR-fallback path). Returns the number of entries reconciled. Wired
        /// to <c>MergeQueueService.ReconcilePullRequestEntriesAsync</c> at server startup.
        /// </summary>
        Func<Task<int>>? OnReconcileMergeEntries { get; set; }

        /// <summary>
        /// Delegate that checks whether a process exit has already been received and is being
        /// handled by the async exit callback. The health check uses this to avoid triggering
        /// recovery for a process whose exit handler is still in progress.
        /// </summary>
        Func<int, bool>? OnIsProcessExitHandled { get; set; }

        /// <summary>
        /// Dispatch a voyage with one or more missions.
        /// Creates the voyage, creates missions, and auto-assigns available captains.
        /// </summary>
        /// <param name="title">Voyage title.</param>
        /// <param name="description">Voyage description.</param>
        /// <param name="vesselId">Target vessel identifier.</param>
        /// <param name="missionDescriptions">List of mission title/description pairs.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created voyage.</returns>
        Task<Voyage> DispatchVoyageAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            CancellationToken token = default);

        /// <summary>
        /// Dispatch a voyage with one or more missions and an ordered set of playbooks.
        /// </summary>
        /// <param name="title">Voyage title.</param>
        /// <param name="description">Voyage description.</param>
        /// <param name="vesselId">Target vessel identifier.</param>
        /// <param name="missionDescriptions">List of mission title/description pairs.</param>
        /// <param name="selectedPlaybooks">Ordered playbooks to apply to every mission in the voyage.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created voyage.</returns>
        Task<Voyage> DispatchVoyageAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            List<SelectedPlaybook>? selectedPlaybooks,
            CancellationToken token = default);

        /// <summary>
        /// Dispatch a new voyage with pipeline support.
        /// When a pipelineId is provided, each mission is wrapped in the pipeline's persona stages.
        /// </summary>
        /// <param name="title">Voyage title.</param>
        /// <param name="description">Voyage description.</param>
        /// <param name="vesselId">Target vessel identifier.</param>
        /// <param name="missionDescriptions">List of mission title/description pairs.</param>
        /// <param name="pipelineId">Optional pipeline ID. Resolved: explicit > vessel default > fleet default > WorkerOnly.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created voyage.</returns>
        Task<Voyage> DispatchVoyageAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            string? pipelineId,
            CancellationToken token = default);

        /// <summary>
        /// Dispatch a new voyage with pipeline support and an ordered set of playbooks.
        /// </summary>
        /// <param name="title">Voyage title.</param>
        /// <param name="description">Voyage description.</param>
        /// <param name="vesselId">Target vessel identifier.</param>
        /// <param name="missionDescriptions">List of mission title/description pairs.</param>
        /// <param name="pipelineId">Optional pipeline ID. Resolved: explicit > vessel default > fleet default > WorkerOnly.</param>
        /// <param name="selectedPlaybooks">Ordered playbooks to apply to every mission in the voyage.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created voyage.</returns>
        Task<Voyage> DispatchVoyageAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            string? pipelineId,
            List<SelectedPlaybook>? selectedPlaybooks,
            CancellationToken token = default);

        /// <summary>
        /// Dispatch a new voyage with pipeline support, playbooks and operator-confirmed stage skips.
        /// The named stages are dropped from the resolved pipeline before any record is created and
        /// the remaining stages chain across the gap; one <c>voyage.stage_skipped</c> event is
        /// recorded per dropped stage. The Judge can never be skipped.
        /// </summary>
        /// <param name="title">Voyage title.</param>
        /// <param name="description">Voyage description.</param>
        /// <param name="vesselId">Target vessel identifier.</param>
        /// <param name="missionDescriptions">List of mission title/description pairs.</param>
        /// <param name="pipelineId">Optional pipeline ID. Resolved: explicit > vessel default > fleet default > WorkerOnly.</param>
        /// <param name="selectedPlaybooks">Ordered playbooks to apply to every mission in the voyage.</param>
        /// <param name="stageSkip">Operator-confirmed stage skips, or null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created voyage.</returns>
        /// <exception cref="StageSkipRefusedException">When the skip names the Judge or a persona not in the pipeline.</exception>
        Task<Voyage> DispatchVoyageAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            string? pipelineId,
            List<SelectedPlaybook>? selectedPlaybooks,
            StageSkipRequest? stageSkip,
            CancellationToken token = default)
        {
            if (StageSkipRequest.HasStages(stageSkip))
                throw new NotSupportedException("This admiral does not support stage skips.");
            return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, pipelineId, selectedPlaybooks, token);
        }

        /// <summary>
        /// Dispatch a voyage and return after the voyage and mission records have been
        /// durably persisted. Assignment/provisioning is queued in the background so
        /// request/stdio callers do not block on first-time worktree setup.
        /// </summary>
        /// <param name="title">Voyage title.</param>
        /// <param name="description">Voyage description.</param>
        /// <param name="vesselId">Target vessel identifier.</param>
        /// <param name="missionDescriptions">List of mission title/description pairs.</param>
        /// <param name="pipelineId">Optional pipeline ID. Resolved using the standard dispatch precedence.</param>
        /// <param name="selectedPlaybooks">Ordered playbooks to apply to every mission in the voyage.</param>
        /// <param name="token">Cancellation token for durable creation only.</param>
        /// <returns>The created voyage, usually still Open until queued assignment starts.</returns>
        Task<Voyage> DispatchVoyageQueuedAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            string? pipelineId,
            List<SelectedPlaybook>? selectedPlaybooks,
            CancellationToken token = default)
        {
            return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, pipelineId, selectedPlaybooks, token);
        }

        /// <summary>
        /// Queued dispatch with operator-confirmed stage skips. Same skip rule as the synchronous
        /// overload that takes a <see cref="StageSkipRequest"/>.
        /// </summary>
        /// <param name="title">Voyage title.</param>
        /// <param name="description">Voyage description.</param>
        /// <param name="vesselId">Target vessel identifier.</param>
        /// <param name="missionDescriptions">List of mission title/description pairs.</param>
        /// <param name="pipelineId">Optional pipeline ID. Resolved using the standard dispatch precedence.</param>
        /// <param name="selectedPlaybooks">Ordered playbooks to apply to every mission in the voyage.</param>
        /// <param name="stageSkip">Operator-confirmed stage skips, or null.</param>
        /// <param name="token">Cancellation token for durable creation only.</param>
        /// <returns>The created voyage, usually still Open until queued assignment starts.</returns>
        /// <exception cref="StageSkipRefusedException">When the skip names the Judge or a persona not in the pipeline.</exception>
        Task<Voyage> DispatchVoyageQueuedAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            string? pipelineId,
            List<SelectedPlaybook>? selectedPlaybooks,
            StageSkipRequest? stageSkip,
            CancellationToken token = default)
        {
            return DispatchVoyageAsync(title, description, vesselId, missionDescriptions, pipelineId, selectedPlaybooks, stageSkip, token);
        }

        /// <summary>
        /// Queued dispatch with stage skips and per-persona captain assignments. The assignments are stored on
        /// the voyage when it is created, before any mission exists, so the first assignment of every mission
        /// resolves its requested captain from them.
        /// </summary>
        /// <param name="title">Voyage title.</param>
        /// <param name="description">Voyage description.</param>
        /// <param name="vesselId">Target vessel identifier.</param>
        /// <param name="missionDescriptions">List of mission title/description pairs.</param>
        /// <param name="pipelineId">Optional pipeline ID. Resolved using the standard dispatch precedence.</param>
        /// <param name="selectedPlaybooks">Ordered playbooks to apply to every mission in the voyage.</param>
        /// <param name="stageSkip">Operator-confirmed stage skips, or null.</param>
        /// <param name="captainOverrides">Per-persona captain assignments, or null.</param>
        /// <param name="token">Cancellation token for durable creation only.</param>
        /// <returns>The created voyage, usually still Open until queued assignment starts.</returns>
        /// <exception cref="NotSupportedException">When assignments are supplied to an admiral that cannot store them.</exception>
        Task<Voyage> DispatchVoyageQueuedAsync(
            string title,
            string description,
            string vesselId,
            List<MissionDescription> missionDescriptions,
            string? pipelineId,
            List<SelectedPlaybook>? selectedPlaybooks,
            StageSkipRequest? stageSkip,
            List<CaptainAssignmentOverride>? captainOverrides,
            CancellationToken token = default)
        {
            if (captainOverrides != null && captainOverrides.Count > 0)
                throw new NotSupportedException("This admiral does not support captain assignments.");
            return DispatchVoyageQueuedAsync(title, description, vesselId, missionDescriptions, pipelineId, selectedPlaybooks, stageSkip, token);
        }

        /// <summary>
        /// Dispatch a single mission and return after the mission record has been
        /// persisted. Assignment/provisioning is queued in the background.
        /// </summary>
        /// <param name="mission">Mission to dispatch.</param>
        /// <param name="token">Cancellation token for durable creation only.</param>
        /// <returns>The created mission, usually still Pending until queued assignment starts.</returns>
        Task<Mission> DispatchMissionQueuedAsync(Mission mission, CancellationToken token = default)
        {
            return DispatchMissionAsync(mission, token);
        }

        /// <summary>
        /// Dispatch a single mission.
        /// </summary>
        /// <param name="mission">Mission to dispatch.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created and potentially assigned mission.</returns>
        Task<Mission> DispatchMissionAsync(Mission mission, CancellationToken token = default);

        /// <summary>
        /// Resolve the pipeline a dispatch should use. Resolution order:
        /// explicit (id or name) &gt; vessel default &gt; fleet default &gt; null.
        /// Returns null when the resolved pipeline is the implicit single-stage
        /// Worker pipeline. Exposed so the alias-dispatch path in McpVoyageTools
        /// can mirror the same expansion semantics as the standard dispatch.
        /// </summary>
        /// <param name="pipelineIdOrName">Optional pipeline id or name.</param>
        /// <param name="vessel">Vessel the dispatch targets (required).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Resolved pipeline or null when no multi-stage pipeline applies.</returns>
        Task<Pipeline?> ResolvePipelineAsync(string? pipelineIdOrName, Vessel vessel, CancellationToken token = default);

        /// <summary>
        /// Get aggregate status across all active work.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Status summary object.</returns>
        Task<ArmadaStatus> GetStatusAsync(CancellationToken token = default);

        /// <summary>
        /// Stop a specific captain gracefully.
        /// </summary>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="token">Cancellation token.</param>
        Task RecallCaptainAsync(string captainId, CancellationToken token = default);

        /// <summary>
        /// Emergency stop all captains.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        Task RecallAllAsync(CancellationToken token = default);

        /// <summary>
        /// Kill the OS processes of all working captains without altering mission or dock state.
        /// Called on Admiral shutdown so agent subprocesses do not survive as orphans; restart
        /// recovery then reconciles the now-dead processes normally. Cross-platform via the
        /// runtime's process-tree kill.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        Task StopAllAgentProcessesAsync(CancellationToken token = default);

        /// <summary>
        /// Run a single health check cycle across all active captains.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        Task HealthCheckAsync(CancellationToken token = default);

        /// <summary>
        /// Reset captains left in Working state with dead processes after a server restart.
        /// Resets orphaned captains to Idle, transitions their active missions back to Pending,
        /// and dispatches any pending missions.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        Task CleanupStaleCaptainsAsync(CancellationToken token = default);

        /// <summary>
        /// Handle an agent process exit detected via the OnProcessExited event.
        /// Transitions the mission to the appropriate state, releases the captain,
        /// and reclaims the dock. An exit from a process the admiral superseded, or from a
        /// process that is no longer the captain's recorded process, is ignored and recorded.
        /// </summary>
        /// <param name="processId">OS process ID that exited.</param>
        /// <param name="exitCode">Exit code, or null if unavailable.</param>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="token">Cancellation token.</param>
        Task HandleProcessExitAsync(int processId, int? exitCode, string captainId, string missionId, CancellationToken token = default);
    }
}
