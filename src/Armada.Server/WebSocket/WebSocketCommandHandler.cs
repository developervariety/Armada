namespace Armada.Server.WebSocket
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;

    /// <summary>
    /// Handles all WebSocket command actions, extracted from the ArmadaWebSocketHub command route.
    /// </summary>
    public class WebSocketCommandHandler
    {
        private readonly IAdmiralService _Admiral;
        private readonly DatabaseBackupService? _Backups;
        private readonly DatabaseDriver _Database;
        private readonly IMergeQueueService _MergeQueue;
        private readonly ArmadaSettings? _Settings;
        private readonly IGitService? _Git;
        private readonly Action? _OnStop;
        private readonly JsonSerializerOptions _JsonOptions;
        private readonly Action<Mission> _BroadcastMissionChange;
        private readonly Action<Voyage> _BroadcastVoyageChange;
        private readonly MissionStatusTransitionService? _StatusTransitions;

        /// <summary>
        /// Instantiate the command handler.
        /// </summary>
        /// <param name="admiral">Admiral service for command handling.</param>
        /// <param name="database">Database driver for data access.</param>
        /// <param name="mergeQueue">Merge queue service.</param>
        /// <param name="settings">Optional Armada settings for log/diff paths.</param>
        /// <param name="git">Optional git service for diff generation.</param>
        /// <param name="onStop">Optional callback invoked when stop_server is requested.</param>
        /// <param name="jsonOptions">JSON serializer options.</param>
        /// <param name="broadcastMissionChange">Callback to broadcast a changed mission to the sessions that may read it.</param>
        /// <param name="broadcastVoyageChange">Callback to broadcast a changed voyage to the sessions that may read it.</param>
        /// <param name="statusTransitions">Shared operator status transition path; without it transitions are refused.</param>
        public WebSocketCommandHandler(
            IAdmiralService admiral,
            DatabaseDriver database,
            IMergeQueueService mergeQueue,
            ArmadaSettings? settings,
            IGitService? git,
            Action? onStop,
            JsonSerializerOptions jsonOptions,
            Action<Mission> broadcastMissionChange,
            Action<Voyage> broadcastVoyageChange,
            MissionStatusTransitionService? statusTransitions = null,
            DatabaseBackupService? backups = null)
        {
            _StatusTransitions = statusTransitions;
            _Backups = backups;
            _Admiral = admiral;
            _Database = database;
            _MergeQueue = mergeQueue;
            _Settings = settings;
            _Git = git;
            _OnStop = onStop;
            _JsonOptions = jsonOptions;
            _BroadcastMissionChange = broadcastMissionChange;
            _BroadcastVoyageChange = broadcastVoyageChange;
        }

        /// <summary>
        /// Reads all text from a file using FileShare.ReadWrite to avoid locking conflicts with writer processes.
        /// </summary>
        private async Task<string> ReadFileSharedAsync(string path)
        {
            using System.IO.FileStream fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            using System.IO.StreamReader reader = new System.IO.StreamReader(fs);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Reads all lines from a file using FileShare.ReadWrite to avoid locking conflicts with writer processes.
        /// </summary>
        private async Task<string[]> ReadLinesSharedAsync(string path)
        {
            List<string> lines = new List<string>();
            using System.IO.FileStream fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            using System.IO.StreamReader reader = new System.IO.StreamReader(fs);
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                lines.Add(line);
            }
            return lines.ToArray();
        }

        /// <summary>
        /// True when a command runs for an authenticated caller. A create records that caller as the owner.
        /// </summary>
        /// <param name="caller">Session caller, or null.</param>
        /// <returns>True for an authenticated caller.</returns>
        private static bool IsAuthenticatedCaller(AuthContext? caller)
        {
            return caller != null && caller.IsAuthenticated;
        }

        /// <summary>
        /// The refusal a create command returns when it has no authenticated caller to own the record.
        /// </summary>
        /// <param name="action">Command action.</param>
        /// <returns>The command error.</returns>
        private static object CreateRequiresCaller(string action)
        {
            return new { type = "command.error", action = action, error = action + " requires an authenticated caller to own the record" };
        }

        /// <summary>
        /// Handle a WebSocket command by dispatching to the appropriate action.
        /// </summary>
        /// <param name="action">The action string from the command.</param>
        /// <param name="command">The deserialized WebSocket command.</param>
        /// <param name="rawBody">The raw JSON body string for data commands.</param>
        /// <param name="caller">The authenticated session caller. Commands that read through a caller-scoped query refuse to run without one.</param>
        /// <returns>The result object to serialize and send back to the client.</returns>
        public async Task<object> HandleCommandAsync(string action, WebSocketCommand command, string rawBody, AuthContext? caller = null)
        {
            switch (action)
            {
                // ── Status & Control ──────────────────────────────────────

                case "status":
                    ArmadaStatus cmdStatus = await _Admiral.GetStatusAsync().ConfigureAwait(false);
                    return new { type = "command.result", action = "status", data = (object)cmdStatus };

                case "stop_captain":
                    string captainId = command.CaptainId ?? "";
                    await _Admiral.RecallCaptainAsync(captainId).ConfigureAwait(false);
                    return new { type = "command.result", action = "stop_captain", data = (object)new { status = "stopped", captainId = captainId } };

                case "stop_all":
                    await _Admiral.RecallAllAsync().ConfigureAwait(false);
                    return new { type = "command.result", action = "stop_all", data = (object)new { status = "all_stopped" } };

                case "stop_server":
                    if (_OnStop != null)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(500).ConfigureAwait(false);
                            _OnStop();
                        });
                    }
                    return new { type = "command.result", action = "stop_server", data = (object)new { status = "shutting_down" } };

                // ── Fleet actions ──────────────────────────────────────────

                case "list_fleets":
                    EnumerationQuery fleetQuery = command.Query ?? new EnumerationQuery();
                    Stopwatch fleetSw = Stopwatch.StartNew();
                    EnumerationResult<Fleet> fleetResult = await _Database.Fleets.EnumerateAsync(fleetQuery).ConfigureAwait(false);
                    fleetResult.TotalMs = Math.Round(fleetSw.Elapsed.TotalMilliseconds, 2);
                    return new { type = "command.result", action = "list_fleets", data = (object)fleetResult };

                case "get_fleet":
                    string getFleetId = command.Id ?? "";
                    Fleet? foundFleet = await _Database.Fleets.ReadAsync(getFleetId).ConfigureAwait(false);
                    if (foundFleet == null)
                        return new { type = "command.error", action = "get_fleet", error = "Fleet not found" };
                    else
                    {
                        List<Vessel> fleetVessels = await _Database.Vessels.EnumerateByFleetAsync(getFleetId).ConfigureAwait(false);
                        return new { type = "command.result", action = "get_fleet", data = (object)new { Fleet = foundFleet, Vessels = fleetVessels } };
                    }

                case "create_fleet":
                    Fleet newFleet = JsonSerializer.Deserialize<WebSocketDataCommand<Fleet>>(rawBody, _JsonOptions)?.Data!;
                    if (!IsAuthenticatedCaller(caller)) return CreateRequiresCaller("create_fleet");
                    newFleet.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller!);
                    newFleet.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller!);
                    newFleet = await _Database.Fleets.CreateAsync(newFleet).ConfigureAwait(false);
                    return new { type = "command.result", action = "create_fleet", data = (object)newFleet };

                case "update_fleet":
                    string updFleetId = command.Id ?? "";
                    Fleet? existFleet = await _Database.Fleets.ReadAsync(updFleetId).ConfigureAwait(false);
                    if (existFleet == null)
                        return new { type = "command.error", action = "update_fleet", error = "Fleet not found" };
                    else
                    {
                        Fleet updFleet = JsonSerializer.Deserialize<WebSocketDataCommand<Fleet>>(rawBody, _JsonOptions)?.Data!;
                        updFleet.Id = updFleetId;
                        updFleet = await _Database.Fleets.UpdateAsync(updFleet).ConfigureAwait(false);
                        return new { type = "command.result", action = "update_fleet", data = (object)updFleet };
                    }

                case "delete_fleet":
                    string delFleetId = command.Id ?? "";
                    await _Database.Fleets.DeleteAsync(delFleetId).ConfigureAwait(false);
                    return new { type = "command.result", action = "delete_fleet", data = (object)new { status = "deleted" } };

                // ── Vessel actions ─────────────────────────────────────────

                case "list_vessels":
                    EnumerationQuery vesselQuery = command.Query ?? new EnumerationQuery();
                    Stopwatch vesselSw = Stopwatch.StartNew();
                    EnumerationResult<Vessel> vesselResult = await _Database.Vessels.EnumerateAsync(vesselQuery).ConfigureAwait(false);
                    vesselResult.TotalMs = Math.Round(vesselSw.Elapsed.TotalMilliseconds, 2);
                    return new { type = "command.result", action = "list_vessels", data = (object)vesselResult };

                case "get_vessel":
                    string getVesselId = command.Id ?? "";
                    Vessel? foundVessel = await _Database.Vessels.ReadAsync(getVesselId).ConfigureAwait(false);
                    if (foundVessel == null)
                        return new { type = "command.error", action = "get_vessel", error = "Vessel not found" };
                    else
                        return new { type = "command.result", action = "get_vessel", data = (object)foundVessel };

                case "create_vessel":
                    Vessel newVessel = JsonSerializer.Deserialize<WebSocketDataCommand<Vessel>>(rawBody, _JsonOptions)?.Data!;
                    if (String.IsNullOrEmpty(newVessel.RepoUrl))
                        return new { type = "command.error", action = "create_vessel", error = "repoUrl is required when creating a vessel" };
                    if (!IsAuthenticatedCaller(caller)) return CreateRequiresCaller("create_vessel");
                    newVessel.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller!);
                    newVessel.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller!);
                    newVessel.NormalizeGitHubTokenOverride();
                    newVessel = await _Database.Vessels.CreateAsync(newVessel).ConfigureAwait(false);
                    return new { type = "command.result", action = "create_vessel", data = (object)newVessel };

                case "update_vessel":
                    string updVesselId = command.Id ?? "";
                    Vessel? existVessel = await _Database.Vessels.ReadAsync(updVesselId).ConfigureAwait(false);
                    if (existVessel == null)
                        return new { type = "command.error", action = "update_vessel", error = "Vessel not found" };
                    else
                    {
                        Vessel updVessel = JsonSerializer.Deserialize<WebSocketDataCommand<Vessel>>(rawBody, _JsonOptions)?.Data!;
                        updVessel.Id = updVesselId;
                        // The token override is write-only: an update that omits it keeps the stored value.
                        updVessel.GitHubTokenOverride = Vessel.ResolveGitHubTokenOverride(
                            existVessel.GitHubTokenOverride, updVessel.GitHubTokenOverrideSpecified, updVessel.GitHubTokenOverride);
                        updVessel = await _Database.Vessels.UpdateAsync(updVessel).ConfigureAwait(false);
                        return new { type = "command.result", action = "update_vessel", data = (object)updVessel };
                    }

                case "update_vessel_context":
                    string ctxVesselId = command.Id ?? "";
                    Vessel? ctxVessel = await _Database.Vessels.ReadAsync(ctxVesselId).ConfigureAwait(false);
                    if (ctxVessel == null)
                        return new { type = "command.error", action = "update_vessel_context", error = "Vessel not found" };
                    else
                    {
                        Vessel ctxPatch = JsonSerializer.Deserialize<WebSocketDataCommand<Vessel>>(rawBody, _JsonOptions)?.Data!;
                        if (ctxPatch.ProjectContext != null)
                            ctxVessel.ProjectContext = ctxPatch.ProjectContext;
                        if (ctxPatch.StyleGuide != null)
                            ctxVessel.StyleGuide = ctxPatch.StyleGuide;
                        ctxVessel = await _Database.Vessels.UpdateAsync(ctxVessel).ConfigureAwait(false);
                        return new { type = "command.result", action = "update_vessel_context", data = (object)ctxVessel };
                    }

                case "delete_vessel":
                {
                    string delVesselId = command.Id ?? "";
                    Vessel? delVessel = await _Database.Vessels.ReadAsync(delVesselId).ConfigureAwait(false);
                    if (delVessel == null)
                        return new { type = "command.error", action = "delete_vessel", error = "Vessel not found" };

                    // Cancel active missions on this vessel
                    try
                    {
                        List<Mission> delVesselMissions = await _Database.Missions.EnumerateByVesselAsync(delVesselId).ConfigureAwait(false);
                        foreach (Mission dvm in delVesselMissions)
                        {
                            if (dvm.Status == MissionStatusEnum.Pending || dvm.Status == MissionStatusEnum.Assigned || dvm.Status == MissionStatusEnum.InProgress)
                            {
                                dvm.Status = MissionStatusEnum.Cancelled;
                                dvm.CompletedUtc = DateTime.UtcNow;
                                dvm.LastUpdateUtc = DateTime.UtcNow;
                                await _Database.Missions.UpdateAsync(dvm).ConfigureAwait(false);
                            }
                        }
                    }
                    catch { }

                    // Clean up docks/worktrees for this vessel
                    try
                    {
                        List<Dock> delVesselDocks = await _Database.Docks.EnumerateByVesselAsync(delVesselId).ConfigureAwait(false);
                        foreach (Dock dvd in delVesselDocks)
                        {
                            if (!String.IsNullOrEmpty(dvd.WorktreePath) && System.IO.Directory.Exists(dvd.WorktreePath))
                            {
                                try { System.IO.Directory.Delete(dvd.WorktreePath, true); }
                                catch { }
                            }
                            await _Database.Docks.DeleteAsync(dvd.Id).ConfigureAwait(false);
                        }
                    }
                    catch { }

                    // Clean up bare repo
                    if (!String.IsNullOrEmpty(delVessel.LocalPath) && System.IO.Directory.Exists(delVessel.LocalPath))
                    {
                        try { System.IO.Directory.Delete(delVessel.LocalPath, true); }
                        catch { }
                    }

                    await _Database.Vessels.DeleteAsync(delVesselId).ConfigureAwait(false);
                    return new { type = "command.result", action = "delete_vessel", data = (object)new { status = "deleted" } };
                }

                // ── Voyage actions ─────────────────────────────────────────

                case "list_voyages":
                    EnumerationQuery voyageQuery = command.Query ?? new EnumerationQuery();
                    Stopwatch voyageSw = Stopwatch.StartNew();
                    EnumerationResult<Voyage> voyageResult = await _Database.Voyages.EnumerateAsync(voyageQuery).ConfigureAwait(false);
                    voyageResult.TotalMs = Math.Round(voyageSw.Elapsed.TotalMilliseconds, 2);
                    return new { type = "command.result", action = "list_voyages", data = (object)voyageResult };

                case "get_voyage":
                    string getVoyageId = command.Id ?? "";
                    Voyage? foundVoyage = await _Database.Voyages.ReadAsync(getVoyageId).ConfigureAwait(false);
                    if (foundVoyage == null)
                        return new { type = "command.error", action = "get_voyage", error = "Voyage not found" };
                    else
                    {
                        EnumerationResult<Mission> voyageMissions = await _Database.Missions.EnumerateSummariesAsync(new EnumerationQuery
                        {
                            VoyageId = getVoyageId,
                            PageSize = 1000
                        }).ConfigureAwait(false);
                        return new { type = "command.result", action = "get_voyage", data = (object)new { voyage = foundVoyage, missions = voyageMissions.Objects } };
                    }

                case "create_voyage":
                    WebSocketVoyageData voyageData = JsonSerializer.Deserialize<WebSocketDataCommand<WebSocketVoyageData>>(rawBody, _JsonOptions)?.Data ?? new WebSocketVoyageData();
                    string voyTitle = voyageData.Title ?? "";
                    string voyDesc = voyageData.Description ?? "";
                    string voyVesselId = voyageData.VesselId ?? "";

                    List<MissionDescription> missionDescs = voyageData.Missions ?? new List<MissionDescription>();

                    Voyage createdVoyage;
                    if (String.IsNullOrEmpty(voyVesselId) || missionDescs.Count == 0)
                    {
                        createdVoyage = new Voyage(voyTitle, voyDesc);
                        if (!IsAuthenticatedCaller(caller)) return CreateRequiresCaller("create_voyage");
                        createdVoyage.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller!);
                        createdVoyage.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller!);
                        createdVoyage = await _Database.Voyages.CreateAsync(createdVoyage).ConfigureAwait(false);
                    }
                    else
                    {
                        try
                        {
                            StageSkipRequest? voyStageSkip = PipelineStageSkip.FromOperator(voyageData.SkipStages, voyageData.SkipStagesReason, caller);
                            if (voyStageSkip == null)
                            {
                                createdVoyage = await _Admiral.DispatchVoyageAsync(voyTitle, voyDesc, voyVesselId, missionDescs).ConfigureAwait(false);
                            }
                            else
                            {
                                // A stage skip only has meaning against a pipeline, so a create_voyage that
                                // names skipStages materialises the vessel's effective pipeline minus those
                                // stages through the same admiral rule the REST and MCP dispatch use.
                                createdVoyage = await _Admiral.DispatchVoyageAsync(voyTitle, voyDesc, voyVesselId, missionDescs, null, null, voyStageSkip).ConfigureAwait(false);
                            }
                        }
                        catch (StageSkipRefusedException refused)
                        {
                            return new
                            {
                                type = "command.error",
                                action = "create_voyage",
                                error = refused.Message,
                                code = refused.Code,
                                persona = refused.Persona
                            };
                        }
                        catch (FleetCapacityAdmissionException capacity)
                        {
                            return new
                            {
                                type = "command.error",
                                action = "create_voyage",
                                error = capacity.Message,
                                code = capacity.Code,
                                activeCount = capacity.ActiveCount,
                                limit = capacity.Limit,
                                candidateVesselId = capacity.CandidateVesselId,
                                laneMembers = capacity.LaneMembers
                            };
                        }
                    }
                    return new { type = "command.result", action = "create_voyage", data = (object)createdVoyage };

                case "cancel_voyage":
                {
                    string cvId = command.Id ?? "";
                    Voyage? cvVoyage = await _Database.Voyages.ReadAsync(cvId).ConfigureAwait(false);
                    if (cvVoyage == null)
                        return new { type = "command.error", action = "cancel_voyage", error = "Voyage not found" };
                    else
                    {
                        cvVoyage.Status = VoyageStatusEnum.Cancelled;
                        cvVoyage.CompletedUtc = DateTime.UtcNow;
                        cvVoyage.LastUpdateUtc = DateTime.UtcNow;
                        await _Database.Voyages.UpdateAsync(cvVoyage).ConfigureAwait(false);
                        List<Mission> cvMissions = await _Database.Missions.EnumerateByVoyageAsync(cvId).ConfigureAwait(false);
                        foreach (Mission m in cvMissions)
                        {
                            if (m.Status == MissionStatusEnum.Pending || m.Status == MissionStatusEnum.Assigned)
                            {
                                if (!String.IsNullOrEmpty(m.CaptainId))
                                {
                                    Captain? captain = await _Database.Captains.ReadAsync(m.CaptainId).ConfigureAwait(false);
                                    if (captain != null && captain.CurrentMissionId == m.Id)
                                    {
                                        List<Mission> otherMissions = (await _Database.Missions.EnumerateByCaptainAsync(captain.Id).ConfigureAwait(false))
                                            .Where(om => om.Id != m.Id && (om.Status == MissionStatusEnum.InProgress || om.Status == MissionStatusEnum.Assigned)).ToList();
                                        if (otherMissions.Count == 0)
                                        {
                                            captain.State = CaptainStateEnum.Idle;
                                            captain.CurrentMissionId = null;
                                            captain.CurrentDockId = null;
                                            captain.ProcessId = null;
                                            captain.RecoveryAttempts = 0;
                                            captain.LastUpdateUtc = DateTime.UtcNow;
                                            await _Database.Captains.UpdateAsync(captain).ConfigureAwait(false);
                                        }
                                    }
                                }

                                m.Status = MissionStatusEnum.Cancelled;
                                m.CompletedUtc = DateTime.UtcNow;
                                m.LastUpdateUtc = DateTime.UtcNow;
                                await _Database.Missions.UpdateAsync(m).ConfigureAwait(false);
                            }
                        }
                        int cvCancelled = cvMissions.Count(m => m.Status == MissionStatusEnum.Cancelled);
                        // Command events follow the changed record's owner, like the same change made through REST.
                        _BroadcastVoyageChange(cvVoyage);
                        foreach (Mission cvCm in cvMissions)
                        {
                            if (cvCm.Status == MissionStatusEnum.Cancelled)
                            {
                                _BroadcastMissionChange(cvCm);
                            }
                        }
                        return new { type = "command.result", action = "cancel_voyage", data = (object)new { Voyage = cvVoyage, CancelledMissions = cvCancelled } };
                    }
                }

                case "purge_voyage":
                {
                    string pvId = command.Id ?? "";
                    Voyage? pvVoyage = await _Database.Voyages.ReadAsync(pvId).ConfigureAwait(false);
                    if (pvVoyage == null)
                        return new { type = "command.error", action = "purge_voyage", error = "Voyage not found" };
                    else if (pvVoyage.Status == VoyageStatusEnum.Open || pvVoyage.Status == VoyageStatusEnum.InProgress)
                        return new { type = "command.error", action = "purge_voyage", error = "Cannot delete voyage while status is " + pvVoyage.Status + ". Cancel the voyage first." };
                    else
                    {
                        List<Mission> pvMissions = await _Database.Missions.EnumerateByVoyageAsync(pvId).ConfigureAwait(false);
                        int pvActiveCount = pvMissions.Count(m => m.Status == MissionStatusEnum.Assigned || m.Status == MissionStatusEnum.InProgress);
                        if (pvActiveCount > 0)
                            return new { type = "command.error", action = "purge_voyage", error = "Cannot delete voyage with " + pvActiveCount + " active mission(s) in Assigned or InProgress status. Cancel or complete them first." };
                        else
                        {
                            foreach (Mission m in pvMissions)
                            {
                                // Clean up associated dock/worktree
                                if (!String.IsNullOrEmpty(m.DockId))
                                {
                                    try
                                    {
                                        Dock? pvDock = await _Database.Docks.ReadAsync(m.DockId).ConfigureAwait(false);
                                        if (pvDock != null)
                                        {
                                            if (!String.IsNullOrEmpty(pvDock.WorktreePath) && System.IO.Directory.Exists(pvDock.WorktreePath))
                                            {
                                                try { System.IO.Directory.Delete(pvDock.WorktreePath, true); }
                                                catch { }
                                            }
                                            await _Database.Docks.DeleteAsync(pvDock.Id).ConfigureAwait(false);
                                        }
                                    }
                                    catch { }
                                }

                                // Clean up log and diff files
                                if (_Settings != null)
                                {
                                    try
                                    {
                                        string pvLogPath = System.IO.Path.Combine(_Settings.LogDirectory, "missions", m.Id + ".log");
                                        if (System.IO.File.Exists(pvLogPath)) System.IO.File.Delete(pvLogPath);
                                    }
                                    catch { }
                                    try
                                    {
                                        string pvDiffPath = System.IO.Path.Combine(_Settings.LogDirectory, "diffs", m.Id + ".diff");
                                        if (System.IO.File.Exists(pvDiffPath)) System.IO.File.Delete(pvDiffPath);
                                    }
                                    catch { }
                                }

                                await _Database.Missions.DeleteAsync(m.Id).ConfigureAwait(false);
                            }
                            await _Database.Voyages.DeleteAsync(pvId).ConfigureAwait(false);
                            return new { type = "command.result", action = "purge_voyage", data = (object)new { status = "deleted", voyageId = pvId, missionsDeleted = pvMissions.Count } };
                        }
                    }
                }

                // ── Mission actions ────────────────────────────────────────

                case "list_missions":
                    EnumerationQuery missionQuery = command.Query ?? new EnumerationQuery();
                    Stopwatch missionSw = Stopwatch.StartNew();
                    EnumerationResult<Mission> missionResult = await _Database.Missions.EnumerateSummariesAsync(missionQuery).ConfigureAwait(false);
                    missionResult.TotalMs = Math.Round(missionSw.Elapsed.TotalMilliseconds, 2);
                    return new { type = "command.result", action = "list_missions", data = (object)missionResult };

                case "list_missions_summary":
                    // Reads through the same caller-scoped query as REST, so a session receives exactly the
                    // summaries REST returns to the same caller. There is no default caller to fall back to.
                    if (caller == null || !caller.IsAuthenticated)
                        return new { type = "command.error", action = "list_missions_summary", error = "list_missions_summary requires an authenticated caller" };
                    EnumerationResult<MissionSummary> summaryResult = await MissionSummaryQuery.EnumerateForCallerAsync(
                        _Database, caller, command.Query ?? new EnumerationQuery()).ConfigureAwait(false);
                    return new { type = "command.result", action = "list_missions_summary", data = (object)summaryResult };

                case "get_mission":
                    string getMissionId = command.Id ?? "";
                    Mission? foundMission = await _Database.Missions.ReadSummaryAsync(getMissionId).ConfigureAwait(false);
                    if (foundMission == null)
                        return new { type = "command.error", action = "get_mission", error = "Mission not found" };
                    else
                        return new { type = "command.result", action = "get_mission", data = (object)foundMission };

                case "create_mission":
                {
                    Mission newMission = JsonSerializer.Deserialize<WebSocketDataCommand<Mission>>(rawBody, _JsonOptions)?.Data!;
                    if (!IsAuthenticatedCaller(caller)) return CreateRequiresCaller("create_mission");
                    newMission.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller!);
                    newMission.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller!);
                    try
                    {
                        newMission = await _Admiral.DispatchMissionAsync(newMission).ConfigureAwait(false);
                    }
                    catch (FleetCapacityAdmissionException capacity)
                    {
                        return new
                        {
                            type = "command.error",
                            action = "create_mission",
                            error = capacity.Message,
                            code = capacity.Code,
                            activeCount = capacity.ActiveCount,
                            limit = capacity.Limit,
                            candidateVesselId = capacity.CandidateVesselId,
                            laneMembers = capacity.LaneMembers
                        };
                    }
                    if (newMission.Status == MissionStatusEnum.Pending)
                    {
                        return new { type = "command.result", action = "create_mission", data = (object)newMission, warning = "Mission created but could not be assigned to any captain. It will be retried on the next health check cycle." };
                    }
                    else
                    {
                        return new { type = "command.result", action = "create_mission", data = (object)newMission };
                    }
                }

                case "update_mission":
                {
                    string updMissionId = command.Id ?? "";
                    Mission? existMission = await _Database.Missions.ReadAsync(updMissionId).ConfigureAwait(false);
                    if (existMission == null)
                        return new { type = "command.error", action = "update_mission", error = "Mission not found" };
                    else
                    {
                        Mission updMission = JsonSerializer.Deserialize<WebSocketDataCommand<Mission>>(rawBody, _JsonOptions)?.Data!;
                        updMission.Id = updMissionId;
                        updMission = await _Database.Missions.UpdateAsync(updMission).ConfigureAwait(false);
                        return new { type = "command.result", action = "update_mission", data = (object)updMission };
                    }
                }

                case "transition_mission_status":
                {
                    string tmId = command.Id ?? "";
                    string tmStatus = command.Status ?? "";
                    Mission? tmMission = await _Database.Missions.ReadAsync(tmId).ConfigureAwait(false);
                    if (tmMission == null)
                    {
                        return new { type = "command.error", action = "transition_mission_status", error = "Mission not found" };
                    }
                    else if (!Enum.TryParse<MissionStatusEnum>(tmStatus, true, out MissionStatusEnum tmNewStatus))
                    {
                        return new { type = "command.error", action = "transition_mission_status", error = "Invalid status: " + tmStatus };
                    }
                    else if (_StatusTransitions == null)
                    {
                        return new { type = "command.error", action = "transition_mission_status", error = MissionStatusTransitionService.UnavailableMessage };
                    }
                    else
                    {
                        // The shared operator transition path applies the same validation, manual
                        // completion gates, landing, and handoff as the REST status route.
                        MissionStatusTransitionResult tmResult = await _StatusTransitions.TransitionAsync(tmMission, tmNewStatus).ConfigureAwait(false);
                        if (tmResult.Outcome == MissionStatusTransitionOutcomeEnum.Applied)
                            return new { type = "command.result", action = "transition_mission_status", data = (object)tmResult.Mission! };
                        return new { type = "command.error", action = "transition_mission_status", error = tmResult.Message, reason = tmResult.Reason };
                    }
                }

                case "cancel_mission":
                {
                    string cmId = command.Id ?? "";
                    Mission? cmMission = await _Database.Missions.ReadAsync(cmId).ConfigureAwait(false);
                    if (cmMission == null)
                        return new { type = "command.error", action = "cancel_mission", error = "Mission not found" };
                    else
                    {
                        if (!String.IsNullOrEmpty(cmMission.CaptainId))
                        {
                            Captain? cmCaptain = await _Database.Captains.ReadAsync(cmMission.CaptainId).ConfigureAwait(false);
                            if (cmCaptain != null && cmCaptain.CurrentMissionId == cmMission.Id)
                            {
                                List<Mission> cmOther = (await _Database.Missions.EnumerateByCaptainAsync(cmCaptain.Id).ConfigureAwait(false))
                                    .Where(om => om.Id != cmMission.Id && (om.Status == MissionStatusEnum.InProgress || om.Status == MissionStatusEnum.Assigned)).ToList();
                                if (cmOther.Count == 0)
                                {
                                    cmCaptain.State = CaptainStateEnum.Idle;
                                    cmCaptain.CurrentMissionId = null;
                                    cmCaptain.CurrentDockId = null;
                                    cmCaptain.ProcessId = null;
                                    cmCaptain.RecoveryAttempts = 0;
                                    cmCaptain.LastUpdateUtc = DateTime.UtcNow;
                                    await _Database.Captains.UpdateAsync(cmCaptain).ConfigureAwait(false);
                                }
                            }
                        }

                        cmMission.Status = MissionStatusEnum.Cancelled;
                        cmMission.CompletedUtc = DateTime.UtcNow;
                        cmMission.LastUpdateUtc = DateTime.UtcNow;
                        cmMission = await _Database.Missions.UpdateAsync(cmMission).ConfigureAwait(false);
                        _BroadcastMissionChange(cmMission);
                        return new { type = "command.result", action = "cancel_mission", data = (object)cmMission };
                    }
                }

                case "purge_mission":
                {
                    string pmId = command.Id ?? "";
                    Mission? pmMission = await _Database.Missions.ReadAsync(pmId).ConfigureAwait(false);
                    if (pmMission == null)
                        return new { type = "command.error", action = "purge_mission", error = "Mission not found" };
                    else
                    {
                        // Clean up associated dock/worktree
                        if (!String.IsNullOrEmpty(pmMission.DockId))
                        {
                            try
                            {
                                Dock? pmDock = await _Database.Docks.ReadAsync(pmMission.DockId).ConfigureAwait(false);
                                if (pmDock != null)
                                {
                                    if (!String.IsNullOrEmpty(pmDock.WorktreePath) && System.IO.Directory.Exists(pmDock.WorktreePath))
                                    {
                                        try { System.IO.Directory.Delete(pmDock.WorktreePath, true); }
                                        catch { }
                                    }
                                    await _Database.Docks.DeleteAsync(pmDock.Id).ConfigureAwait(false);
                                }
                            }
                            catch { }
                        }

                        // Clean up log and diff files
                        if (_Settings != null)
                        {
                            try
                            {
                                string pmLogPath = System.IO.Path.Combine(_Settings.LogDirectory, "missions", pmId + ".log");
                                if (System.IO.File.Exists(pmLogPath)) System.IO.File.Delete(pmLogPath);
                            }
                            catch { }
                            try
                            {
                                string pmDiffPath = System.IO.Path.Combine(_Settings.LogDirectory, "diffs", pmId + ".diff");
                                if (System.IO.File.Exists(pmDiffPath)) System.IO.File.Delete(pmDiffPath);
                            }
                            catch { }
                        }

                        await _Database.Missions.DeleteAsync(pmId).ConfigureAwait(false);
                        return new { type = "command.result", action = "purge_mission", data = (object)new { status = "deleted", missionId = pmId } };
                    }
                }

                case "restart_mission":
                {
                    string rmId = command.Id ?? "";
                    Mission? rmMission = await _Database.Missions.ReadAsync(rmId).ConfigureAwait(false);
                    if (rmMission == null)
                        return new { type = "command.error", action = "restart_mission", error = "Mission not found" };
                    else if (rmMission.Status != MissionStatusEnum.Failed && rmMission.Status != MissionStatusEnum.Cancelled && rmMission.Status != MissionStatusEnum.LandingFailed)
                        return new { type = "command.error", action = "restart_mission", error = "Only Failed, LandingFailed, or Cancelled missions can be restarted" };
                    else
                    {
                        WebSocketDataCommand<MissionRestartData>? rmData = null;
                        try { rmData = JsonSerializer.Deserialize<WebSocketDataCommand<MissionRestartData>>(rawBody, _JsonOptions); } catch { }
                        if (rmData?.Data != null)
                        {
                            if (!String.IsNullOrEmpty(rmData.Data.Title)) rmMission.Title = rmData.Data.Title;
                            if (!String.IsNullOrEmpty(rmData.Data.Description)) rmMission.Description = rmData.Data.Description;
                        }

                        try
                        {
                            MissionRestartService restarts = new MissionRestartService(_Database, _Settings ?? new ArmadaSettings());
                            rmMission = await restarts.RestartAsync(
                                rmMission,
                                rmMission.Title,
                                rmMission.Description,
                                allowLandingFailed: true).ConfigureAwait(false);
                        }
                        catch (FleetCapacityAdmissionException capacity)
                        {
                            return new
                            {
                                type = "command.error",
                                action = "restart_mission",
                                error = capacity.Message,
                                code = capacity.Code,
                                activeCount = capacity.ActiveCount,
                                limit = capacity.Limit,
                                candidateVesselId = capacity.CandidateVesselId,
                                laneMembers = capacity.LaneMembers
                            };
                        }

                        // The restart signal belongs to the mission it reports, so the mission's owner sees it.
                        Signal rmSignal = new Signal(SignalTypeEnum.Progress, "Mission " + rmId + " restarted");
                        rmSignal.TenantId = rmMission.TenantId;
                        rmSignal.UserId = rmMission.UserId;
                        await _Database.Signals.CreateAsync(rmSignal).ConfigureAwait(false);

                        _BroadcastMissionChange(rmMission);
                        return new { type = "command.result", action = "restart_mission", data = (object)rmMission };
                    }
                }

                case "get_mission_diff":
                {
                    string mdId = command.Id ?? "";
                    Mission? mdMission = await _Database.Missions.ReadAsync(mdId).ConfigureAwait(false);
                    if (mdMission == null)
                        return new { type = "command.error", action = "get_mission_diff", error = "Mission not found" };
                    else if (_Settings == null)
                        return new { type = "command.error", action = "get_mission_diff", error = "Diff not available — settings not configured" };
                    else
                    {
                        string savedDiffPath = System.IO.Path.Combine(_Settings.LogDirectory, "diffs", mdId + ".diff");
                        if (System.IO.File.Exists(savedDiffPath))
                        {
                            string savedDiff = await ReadFileSharedAsync(savedDiffPath).ConfigureAwait(false);
                            return new { type = "command.result", action = "get_mission_diff", data = (object)new { MissionId = mdId, Branch = mdMission.BranchName ?? "", Diff = savedDiff } };
                        }
                        else if (!String.IsNullOrEmpty(mdMission.DiffSnapshot))
                        {
                            return new { type = "command.result", action = "get_mission_diff", data = (object)new { MissionId = mdId, Branch = mdMission.BranchName ?? "", Diff = mdMission.DiffSnapshot } };
                        }
                        else
                        {
                            Dock? mdDock = null;
                            if (!String.IsNullOrEmpty(mdMission.DockId))
                            {
                                mdDock = await _Database.Docks.ReadAsync(mdMission.DockId).ConfigureAwait(false);
                            }
                            if (mdDock == null && !String.IsNullOrEmpty(mdMission.CaptainId))
                            {
                                Captain? mdCaptain = await _Database.Captains.ReadAsync(mdMission.CaptainId).ConfigureAwait(false);
                                if (mdCaptain != null && !String.IsNullOrEmpty(mdCaptain.CurrentDockId))
                                    mdDock = await _Database.Docks.ReadAsync(mdCaptain.CurrentDockId).ConfigureAwait(false);
                            }
                            if (mdDock == null && !String.IsNullOrEmpty(mdMission.BranchName) && !String.IsNullOrEmpty(mdMission.VesselId))
                            {
                                List<Dock> mdDocks = await _Database.Docks.EnumerateByVesselAsync(mdMission.VesselId).ConfigureAwait(false);
                                mdDock = mdDocks.FirstOrDefault(d => d.BranchName == mdMission.BranchName && d.Active);
                            }
                            if (mdDock == null || String.IsNullOrEmpty(mdDock.WorktreePath) || !System.IO.Directory.Exists(mdDock.WorktreePath))
                                return new { type = "command.error", action = "get_mission_diff", error = "No diff available — worktree was already reclaimed and no saved diff exists" };
                            else if (_Git == null)
                                return new { type = "command.error", action = "get_mission_diff", error = "Git service not available" };
                            else
                            {
                                string baseBranch = "main";
                                if (!String.IsNullOrEmpty(mdMission.VesselId))
                                {
                                    Vessel? mdVessel = await _Database.Vessels.ReadAsync(mdMission.VesselId).ConfigureAwait(false);
                                    if (mdVessel != null) baseBranch = mdVessel.DefaultBranch;
                                }
                                string diff = await _Git.DiffAsync(mdDock.WorktreePath, baseBranch).ConfigureAwait(false);
                                return new { type = "command.result", action = "get_mission_diff", data = (object)new { MissionId = mdId, Branch = mdDock.BranchName ?? "", Diff = diff } };
                            }
                        }
                    }
                }

                case "get_mission_log":
                {
                    string mlId = command.Id ?? "";
                    Mission? mlMission = await _Database.Missions.ReadAsync(mlId).ConfigureAwait(false);
                    if (mlMission == null)
                        return new { type = "command.error", action = "get_mission_log", error = "Mission not found" };
                    else if (_Settings == null)
                        return new { type = "command.error", action = "get_mission_log", error = "Logs not available — settings not configured" };
                    else
                    {
                        string mlLogPath = System.IO.Path.Combine(_Settings.LogDirectory, "missions", mlId + ".log");
                        if (!System.IO.File.Exists(mlLogPath))
                            return new { type = "command.result", action = "get_mission_log", data = (object)new { MissionId = mlId, Log = "", Lines = 0, TotalLines = 0 } };
                        else
                        {
                            string[] mlAllLines = RuntimeLogNoiseFilter.Filter(
                                await ReadLinesSharedAsync(mlLogPath).ConfigureAwait(false));
                            int mlTotalLines = mlAllLines.Length;
                            int mlOffset = command.Offset ?? 0;
                            int mlLineCount = command.Lines ?? 100;
                            string[] mlSlice = mlAllLines.Skip(mlOffset).Take(mlLineCount).ToArray();
                            string mlLog = String.Join("\n", mlSlice);
                            return new { type = "command.result", action = "get_mission_log", data = (object)new { MissionId = mlId, Log = mlLog, Lines = mlSlice.Length, TotalLines = mlTotalLines } };
                        }
                    }
                }

                // ── Captain actions ────────────────────────────────────────

                case "list_captains":
                    EnumerationQuery captainQuery = command.Query ?? new EnumerationQuery();
                    Stopwatch captainSw = Stopwatch.StartNew();
                    EnumerationResult<Captain> captainResult = await _Database.Captains.EnumerateAsync(captainQuery).ConfigureAwait(false);
                    captainResult.TotalMs = Math.Round(captainSw.Elapsed.TotalMilliseconds, 2);
                    return new { type = "command.result", action = "list_captains", data = (object)captainResult };

                case "get_captain":
                    string getCaptainId = command.Id ?? "";
                    Captain? foundCaptain = await _Database.Captains.ReadAsync(getCaptainId).ConfigureAwait(false);
                    if (foundCaptain == null)
                        return new { type = "command.error", action = "get_captain", error = "Captain not found" };
                    else
                        return new { type = "command.result", action = "get_captain", data = (object)foundCaptain };

                case "create_captain":
                {
                    Captain? newCaptainInput = JsonSerializer.Deserialize<WebSocketDataCommand<Captain>>(rawBody, _JsonOptions)?.Data;
                    if (newCaptainInput == null)
                        return new { type = "command.error", action = "create_captain", error = "Captain data is required" };
                    string? createOwnedFieldError = CaptainInputMapping.FindServerOwnedFieldViolation(
                        JsonSerializer.Deserialize<WebSocketDataCommand<CaptainServerOwnedFields>>(rawBody, _JsonOptions)?.Data, null);
                    if (createOwnedFieldError != null)
                        return new { type = "command.error", action = "create_captain", error = createOwnedFieldError };
                    Captain captainToCreate = CaptainInputMapping.ForCreate(newCaptainInput);
                    if (!IsAuthenticatedCaller(caller)) return CreateRequiresCaller("create_captain");
                    captainToCreate.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller!);
                    captainToCreate.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller!);
                    Captain newCaptain = await _Database.Captains.CreateAsync(captainToCreate).ConfigureAwait(false);
                    return new { type = "command.result", action = "create_captain", data = (object)newCaptain };
                }

                case "update_captain":
                {
                    string updCptId = command.Id ?? "";
                    Captain? existCpt = await _Database.Captains.ReadAsync(updCptId).ConfigureAwait(false);
                    if (existCpt == null)
                        return new { type = "command.error", action = "update_captain", error = "Captain not found" };
                    else
                    {
                        Captain? updCptInput = JsonSerializer.Deserialize<WebSocketDataCommand<Captain>>(rawBody, _JsonOptions)?.Data;
                        if (updCptInput == null)
                            return new { type = "command.error", action = "update_captain", error = "Captain data is required" };
                        string? updateOwnedFieldError = CaptainInputMapping.FindServerOwnedFieldViolation(
                            JsonSerializer.Deserialize<WebSocketDataCommand<CaptainServerOwnedFields>>(rawBody, _JsonOptions)?.Data, existCpt);
                        if (updateOwnedFieldError != null)
                            return new { type = "command.error", action = "update_captain", error = updateOwnedFieldError };
                        Captain updCpt = await _Database.Captains.UpdateAsync(CaptainInputMapping.ForUpdate(existCpt, updCptInput)).ConfigureAwait(false);
                        return new { type = "command.result", action = "update_captain", data = (object)updCpt };
                    }
                }

                case "delete_captain":
                {
                    string delCptId = command.Id ?? "";
                    Captain? delCpt = await _Database.Captains.ReadAsync(delCptId).ConfigureAwait(false);
                    if (delCpt == null)
                        return new { type = "command.error", action = "delete_captain", error = "Captain not found" };
                    else if (delCpt.State == CaptainStateEnum.Working)
                        return new { type = "command.error", action = "delete_captain", error = "Cannot delete captain while state is Working. Stop the captain first." };
                    else
                    {
                        List<Mission> delCptMissions = await _Database.Missions.EnumerateByCaptainAsync(delCptId).ConfigureAwait(false);
                        int delCptActiveCount = delCptMissions.Count(m => m.Status == MissionStatusEnum.Assigned || m.Status == MissionStatusEnum.InProgress);
                        if (delCptActiveCount > 0)
                            return new { type = "command.error", action = "delete_captain", error = "Cannot delete captain with " + delCptActiveCount + " active mission(s) in Assigned or InProgress status. Cancel or complete them first." };
                        else
                        {
                            await _Database.Captains.DeleteAsync(delCptId).ConfigureAwait(false);
                            return new { type = "command.result", action = "delete_captain", data = (object)new { status = "deleted" } };
                        }
                    }
                }

                case "get_captain_log":
                {
                    string clId = command.Id ?? "";
                    Captain? clCaptain = await _Database.Captains.ReadAsync(clId).ConfigureAwait(false);
                    if (clCaptain == null)
                        return new { type = "command.error", action = "get_captain_log", error = "Captain not found" };
                    else if (_Settings == null)
                        return new { type = "command.error", action = "get_captain_log", error = "Logs not available — settings not configured" };
                    else
                    {
                        string clPointerPath = System.IO.Path.Combine(_Settings.LogDirectory, "captains", clId + ".current");
                        string? clLogPath = null;
                        if (System.IO.File.Exists(clPointerPath))
                        {
                            string clTarget = (await ReadFileSharedAsync(clPointerPath).ConfigureAwait(false)).Trim();
                            if (System.IO.File.Exists(clTarget))
                                clLogPath = clTarget;
                        }
                        if (clLogPath == null)
                            return new { type = "command.result", action = "get_captain_log", data = (object)new { CaptainId = clId, Log = "", Lines = 0, TotalLines = 0 } };
                        else
                        {
                            string[] clAllLines = await ReadLinesSharedAsync(clLogPath).ConfigureAwait(false);
                            int clTotalLines = clAllLines.Length;
                            int clOffset = command.Offset ?? 0;
                            int clLineCount = command.Lines ?? 100;
                            string[] clSlice = clAllLines.Skip(clOffset).Take(clLineCount).ToArray();
                            string clLog = String.Join("\n", clSlice);
                            return new { type = "command.result", action = "get_captain_log", data = (object)new { CaptainId = clId, Log = clLog, Lines = clSlice.Length, TotalLines = clTotalLines } };
                        }
                    }
                }

                // ── Signal actions ─────────────────────────────────────────

                case "list_signals":
                    EnumerationQuery signalQuery = command.Query ?? new EnumerationQuery();
                    Stopwatch signalSw = Stopwatch.StartNew();
                    EnumerationResult<Signal> signalResult = await _Database.Signals.EnumerateAsync(signalQuery).ConfigureAwait(false);
                    signalResult.TotalMs = Math.Round(signalSw.Elapsed.TotalMilliseconds, 2);
                    return new { type = "command.result", action = "list_signals", data = (object)signalResult };

                case "send_signal":
                    Signal newSignal = JsonSerializer.Deserialize<WebSocketDataCommand<Signal>>(rawBody, _JsonOptions)?.Data!;
                    if (!IsAuthenticatedCaller(caller)) return CreateRequiresCaller("send_signal");
                    newSignal.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller!);
                    newSignal.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller!);
                    newSignal = await _Database.Signals.CreateAsync(newSignal).ConfigureAwait(false);
                    return new { type = "command.result", action = "send_signal", data = (object)newSignal };

                // ── Event actions ──────────────────────────────────────────

                case "list_events":
                    EnumerationQuery eventQuery = command.Query ?? new EnumerationQuery();
                    Stopwatch eventSw = Stopwatch.StartNew();
                    EnumerationResult<ArmadaEvent> eventResult = await _Database.Events.EnumerateAsync(eventQuery).ConfigureAwait(false);
                    eventResult.TotalMs = Math.Round(eventSw.Elapsed.TotalMilliseconds, 2);
                    return new { type = "command.result", action = "list_events", data = (object)eventResult };

                // ── Dock actions ───────────────────────────────────────────

                case "list_docks":
                    EnumerationQuery dockQuery = command.Query ?? new EnumerationQuery();
                    Stopwatch dockSw = Stopwatch.StartNew();
                    EnumerationResult<Dock> dockResult = await _Database.Docks.EnumerateAsync(dockQuery).ConfigureAwait(false);
                    dockResult.TotalMs = Math.Round(dockSw.Elapsed.TotalMilliseconds, 2);
                    return new { type = "command.result", action = "list_docks", data = (object)dockResult };

                // ── Merge Queue actions ───────────────────────────────────

                case "list_merge_queue":
                {
                    EnumerationQuery mergeQuery = command.Query ?? new EnumerationQuery();
                    Stopwatch mergeSw = Stopwatch.StartNew();
                    List<MergeEntry> mergeAll = await _MergeQueue.ListAsync().ConfigureAwait(false);
                    int mergeTotal = mergeAll.Count;
                    List<MergeEntry> mergePage = mergeAll.Skip(mergeQuery.Offset).Take(mergeQuery.PageSize).ToList();
                    EnumerationResult<MergeEntry> mergeResult = EnumerationResult<MergeEntry>.Create(mergeQuery, mergePage, mergeTotal);
                    mergeResult.TotalMs = Math.Round(mergeSw.Elapsed.TotalMilliseconds, 2);
                    return new { type = "command.result", action = "list_merge_queue", data = (object)mergeResult };
                }

                case "get_merge_entry":
                    string meId = command.Id ?? "";
                    MergeEntry? foundEntry = await _MergeQueue.GetAsync(meId).ConfigureAwait(false);
                    if (foundEntry == null)
                        return new { type = "command.error", action = "get_merge_entry", error = "Merge entry not found" };
                    else
                        return new { type = "command.result", action = "get_merge_entry", data = (object)foundEntry };

                case "enqueue_merge":
                    MergeEntry newEntry = JsonSerializer.Deserialize<WebSocketDataCommand<MergeEntry>>(rawBody, _JsonOptions)?.Data!;
                    if (!IsAuthenticatedCaller(caller)) return CreateRequiresCaller("enqueue_merge");
                    newEntry.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller!);
                    newEntry.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller!);
                    newEntry = await _MergeQueue.EnqueueAsync(newEntry).ConfigureAwait(false);
                    return new { type = "command.result", action = "enqueue_merge", data = (object)newEntry };

                case "cancel_merge":
                    string cmEntryId = command.Id ?? "";
                    await _MergeQueue.CancelAsync(cmEntryId).ConfigureAwait(false);
                    return new { type = "command.result", action = "cancel_merge", data = (object)new { status = "cancelled" } };

                case "process_merge_queue":
                    await _MergeQueue.ProcessQueueAsync().ConfigureAwait(false);
                    return new { type = "command.result", action = "process_merge_queue", data = (object)new { status = "processed" } };

                // ── Enumerate ────────────────────────────────────────────

                case "enumerate":
                {
                    string entityType = (command.EntityType ?? "").ToLowerInvariant();
                    EnumerationQuery enumQuery = command.Query ?? new EnumerationQuery();
                    Stopwatch enumSw = Stopwatch.StartNew();

                    object? enumData = null;
                    switch (entityType)
                    {
                        case "fleets":
                        case "fleet":
                            EnumerationResult<Fleet> enumFleets = await _Database.Fleets.EnumerateAsync(enumQuery).ConfigureAwait(false);
                            enumFleets.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                            enumData = enumFleets;
                            break;
                        case "vessels":
                        case "vessel":
                            EnumerationResult<Vessel> enumVessels = await _Database.Vessels.EnumerateAsync(enumQuery).ConfigureAwait(false);
                            enumVessels.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                            enumData = enumVessels;
                            break;
                        case "captains":
                        case "captain":
                            EnumerationResult<Captain> enumCaptains = await _Database.Captains.EnumerateAsync(enumQuery).ConfigureAwait(false);
                            enumCaptains.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                            enumData = enumCaptains;
                            break;
                        case "missions":
                        case "mission":
                            EnumerationResult<Mission> enumMissions = await _Database.Missions.EnumerateSummariesAsync(enumQuery).ConfigureAwait(false);
                            enumMissions.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                            enumData = enumMissions;
                            break;
                        case "voyages":
                        case "voyage":
                            EnumerationResult<Voyage> enumVoyages = await _Database.Voyages.EnumerateAsync(enumQuery).ConfigureAwait(false);
                            enumVoyages.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                            enumData = enumVoyages;
                            break;
                        case "docks":
                        case "dock":
                            EnumerationResult<Dock> enumDocks = await _Database.Docks.EnumerateAsync(enumQuery).ConfigureAwait(false);
                            enumDocks.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                            enumData = enumDocks;
                            break;
                        case "signals":
                        case "signal":
                            EnumerationResult<Signal> enumSignals = await _Database.Signals.EnumerateAsync(enumQuery).ConfigureAwait(false);
                            enumSignals.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                            enumData = enumSignals;
                            break;
                        case "events":
                        case "event":
                            EnumerationResult<ArmadaEvent> enumEvents = await _Database.Events.EnumerateAsync(enumQuery).ConfigureAwait(false);
                            enumEvents.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                            enumData = enumEvents;
                            break;
                        case "merge_queue":
                        case "merge-queue":
                        case "mergequeue":
                            List<MergeEntry> enumMqAll = await _MergeQueue.ListAsync().ConfigureAwait(false);
                            int enumMqTotal = enumMqAll.Count;
                            List<MergeEntry> enumMqPage = enumMqAll.Skip(enumQuery.Offset).Take(enumQuery.PageSize).ToList();
                            EnumerationResult<MergeEntry> enumMerge = EnumerationResult<MergeEntry>.Create(enumQuery, enumMqPage, enumMqTotal);
                            enumMerge.TotalMs = Math.Round(enumSw.Elapsed.TotalMilliseconds, 2);
                            enumData = enumMerge;
                            break;
                    }

                    if (enumData == null)
                        return new { type = "command.error", action = "enumerate", error = "Unknown entity type: " + entityType + ". Valid types: fleets, vessels, captains, missions, voyages, docks, signals, events, merge_queue" };
                    else
                        return new { type = "command.result", action = "enumerate", data = enumData };
                }

                // ── Backup & Restore ─────────────────────────────────────

                case "backup":
                {
                    if (_Backups == null) return new { type = "command.error", action = "backup", error = "backup_unavailable" };
                    try
                    {
                        DatabaseBackupResult backupData = await _Backups.BackupAsync(command.OutputPath).ConfigureAwait(false);
                        return new { type = "command.result", action = "backup", data = (object)backupData };
                    }
                    catch (DatabaseBackupException ex)
                    {
                        return new { type = "command.error", action = "backup", error = ex.FailureReason };
                    }
                }

                case "restore":
                {
                    string restoreFilePath = command.FilePath ?? "";
                    if (String.IsNullOrEmpty(restoreFilePath))
                    {
                        return new { type = "command.error", action = "restore", error = "filePath is required" };
                    }
                    if (_Backups == null) return new { type = "command.error", action = "restore", error = "backup_unavailable" };
                    try
                    {
                        DatabaseRestoreResult restoreData = await _Backups.RestoreAsync(restoreFilePath).ConfigureAwait(false);
                        return new { type = "command.result", action = "restore", data = (object)restoreData };
                    }
                    catch (DatabaseBackupException ex)
                    {
                        return new { type = "command.error", action = "restore", error = ex.FailureReason };
                    }
                }

                // ── Personas ──────────────────────────────────────────────

                case "get_persona":
                    string getPersonaName = command.Id ?? "";
                    Persona? foundPersona = await _Database.Personas.ReadByNameAsync(getPersonaName).ConfigureAwait(false);
                    if (foundPersona == null)
                        return new { type = "command.error", action = "get_persona", error = "Persona not found" };
                    return new { type = "command.result", action = "get_persona", data = (object)foundPersona };

                case "create_persona":
                    Persona newPersona = JsonSerializer.Deserialize<WebSocketDataCommand<Persona>>(rawBody, _JsonOptions)?.Data!;
                    if (!IsAuthenticatedCaller(caller)) return CreateRequiresCaller("create_persona");
                    newPersona.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller!);
                    newPersona.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller!);
                    newPersona = await _Database.Personas.CreateAsync(newPersona).ConfigureAwait(false);
                    return new { type = "command.result", action = "create_persona", data = (object)newPersona };

                case "update_persona":
                    string updPersonaName = command.Id ?? "";
                    Persona? existPersona = await _Database.Personas.ReadByNameAsync(updPersonaName).ConfigureAwait(false);
                    if (existPersona == null)
                        return new { type = "command.error", action = "update_persona", error = "Persona not found" };
                    else
                    {
                        Persona patchPersona = JsonSerializer.Deserialize<WebSocketDataCommand<Persona>>(rawBody, _JsonOptions)?.Data!;
                        if (patchPersona.Description != null) existPersona.Description = patchPersona.Description;
                        if (patchPersona.PromptTemplateName != null) existPersona.PromptTemplateName = patchPersona.PromptTemplateName;
                        existPersona = await _Database.Personas.UpdateAsync(existPersona).ConfigureAwait(false);
                        return new { type = "command.result", action = "update_persona", data = (object)existPersona };
                    }

                case "delete_persona":
                    string delPersonaName = command.Id ?? "";
                    Persona? delPersona = await _Database.Personas.ReadByNameAsync(delPersonaName).ConfigureAwait(false);
                    if (delPersona == null)
                        return new { type = "command.error", action = "delete_persona", error = "Persona not found" };
                    if (delPersona.IsBuiltIn)
                        return new { type = "command.error", action = "delete_persona", error = "Cannot delete built-in persona" };
                    await _Database.Personas.DeleteAsync(delPersona.Id).ConfigureAwait(false);
                    return new { type = "command.result", action = "delete_persona", data = (object)new { Status = "deleted", Name = delPersonaName } };

                // ── Prompt Templates ─────────────────────────────────────────

                case "get_prompt_template":
                    string getTemplateName = command.Id ?? "";
                    PromptTemplate? foundTemplate = await _Database.PromptTemplates.ReadByNameAsync(getTemplateName).ConfigureAwait(false);
                    if (foundTemplate == null)
                        return new { type = "command.error", action = "get_prompt_template", error = "Prompt template not found" };
                    return new { type = "command.result", action = "get_prompt_template", data = (object)foundTemplate };

                case "update_prompt_template":
                    string updTemplateName = command.Id ?? "";
                    PromptTemplate? existTemplate = await _Database.PromptTemplates.ReadByNameAsync(updTemplateName).ConfigureAwait(false);
                    if (existTemplate == null)
                        return new { type = "command.error", action = "update_prompt_template", error = "Prompt template not found" };
                    else
                    {
                        PromptTemplate patchTemplate = JsonSerializer.Deserialize<WebSocketDataCommand<PromptTemplate>>(rawBody, _JsonOptions)?.Data!;
                        if (patchTemplate.Content != null) existTemplate.Content = patchTemplate.Content;
                        if (patchTemplate.Description != null) existTemplate.Description = patchTemplate.Description;
                        existTemplate = await _Database.PromptTemplates.UpdateAsync(existTemplate).ConfigureAwait(false);
                        return new { type = "command.result", action = "update_prompt_template", data = (object)existTemplate };
                    }

                // ── Pipelines ────────────────────────────────────────────────

                case "get_pipeline":
                    string getPipelineName = command.Id ?? "";
                    Pipeline? foundPipeline = await _Database.Pipelines.ReadByNameAsync(getPipelineName).ConfigureAwait(false);
                    if (foundPipeline == null)
                        return new { type = "command.error", action = "get_pipeline", error = "Pipeline not found" };
                    return new { type = "command.result", action = "get_pipeline", data = (object)foundPipeline };

                case "create_pipeline":
                    Pipeline newPipeline = JsonSerializer.Deserialize<WebSocketDataCommand<Pipeline>>(rawBody, _JsonOptions)?.Data!;
                    if (!IsAuthenticatedCaller(caller)) return CreateRequiresCaller("create_pipeline");
                    newPipeline.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(caller!);
                    newPipeline.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(caller!);
                    newPipeline = await _Database.Pipelines.CreateAsync(newPipeline).ConfigureAwait(false);
                    return new { type = "command.result", action = "create_pipeline", data = (object)newPipeline };

                case "update_pipeline":
                    string updPipelineName = command.Id ?? "";
                    Pipeline? existPipeline = await _Database.Pipelines.ReadByNameAsync(updPipelineName).ConfigureAwait(false);
                    if (existPipeline == null)
                        return new { type = "command.error", action = "update_pipeline", error = "Pipeline not found" };
                    else
                    {
                        Pipeline patchPipeline = JsonSerializer.Deserialize<WebSocketDataCommand<Pipeline>>(rawBody, _JsonOptions)?.Data!;
                        if (patchPipeline.Description != null) existPipeline.Description = patchPipeline.Description;
                        if (patchPipeline.Stages != null && patchPipeline.Stages.Count > 0)
                        {
                            existPipeline.Stages = patchPipeline.Stages;
                            foreach (PipelineStage stage in existPipeline.Stages)
                                stage.PipelineId = existPipeline.Id;
                        }
                        existPipeline = await _Database.Pipelines.UpdateAsync(existPipeline).ConfigureAwait(false);
                        return new { type = "command.result", action = "update_pipeline", data = (object)existPipeline };
                    }

                case "delete_pipeline":
                    string delPipelineName = command.Id ?? "";
                    Pipeline? delPipeline = await _Database.Pipelines.ReadByNameAsync(delPipelineName).ConfigureAwait(false);
                    if (delPipeline == null)
                        return new { type = "command.error", action = "delete_pipeline", error = "Pipeline not found" };
                    if (delPipeline.IsBuiltIn)
                        return new { type = "command.error", action = "delete_pipeline", error = "Cannot delete built-in pipeline" };
                    await _Database.Pipelines.DeleteAsync(delPipeline.Id).ConfigureAwait(false);
                    return new { type = "command.result", action = "delete_pipeline", data = (object)new { Status = "deleted", Name = delPipelineName } };

                // ── Default ────────────────────────────────────────────────

                default:
                    return new { type = "command.error", action = action, error = "Unknown action: " + action };
            }
        }

    }
}
