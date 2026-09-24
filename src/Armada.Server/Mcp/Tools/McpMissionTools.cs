namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using Armada.Runtimes;

    /// <summary>
    /// Registers MCP tools for mission operations (status, create, update, cancel, purge, restart, transition, diff, log).
    /// </summary>
    public static class McpMissionTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Registers mission MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for mission data access.</param>
        /// <param name="admiral">Admiral service for mission orchestration.</param>
        /// <param name="settings">Armada settings, or null if unavailable.</param>
        /// <param name="git">Git service for diff operations, or null if unavailable.</param>
        /// <param name="landingService">Optional landing service for mission landing operations.</param>
        /// <param name="onStopCaptain">Optional callback that kills a captain's agent process by captain id.
        /// Invoked from armada_cancel_mission when the captain is currently running this mission so
        /// the agent process actually exits instead of staying orphaned in Working state.</param>
        /// <param name="statusTransitions">Shared operator status transition path used by
        /// armada_transition_mission_status; without it every transition is refused.</param>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            IAdmiralService admiral,
            ArmadaSettings? settings,
            IGitService? git,
            ILandingService? landingService = null,
            Func<string, Task>? onStopCaptain = null,
            MissionStatusTransitionService? statusTransitions = null)
        {
            register(
                "armada_mission_status",
                "Get status of a specific mission",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission ID (msn_ prefix)" }
                    },
                    required = new[] { "missionId" }
                },
                async (args) =>
                {
                    MissionIdArgs request = JsonSerializer.Deserialize<MissionIdArgs>(args!.Value, _JsonOptions)!;
                    string missionId = request.MissionId;
                    Mission? mission = await database.Missions.ReadSummaryAsync(missionId).ConfigureAwait(false);
                    if (mission == null) return (object)new { Error = "Mission not found" };
                    try
                    {
                        List<ArmadaEvent> missionEvents = await database.Events.EnumerateByMissionAsync(
                            missionId, 50).ConfigureAwait(false);
                        ArmadaEvent? packEvent = null;
                        ArmadaEvent? budgetEvent = null;
                        ArmadaEvent? launchBudgetEvent = null;
                        foreach (ArmadaEvent evt in missionEvents)
                        {
                            if (evt.EventType == "mission.context_pack_usage" &&
                                (packEvent == null || evt.CreatedUtc > packEvent.CreatedUtc))
                            {
                                packEvent = evt;
                            }
                            else if (evt.EventType == "mission.prompt_budget" &&
                                     (budgetEvent == null || evt.CreatedUtc > budgetEvent.CreatedUtc))
                            {
                                budgetEvent = evt;
                            }
                            else if (evt.EventType == "mission.launch_prompt_budget" &&
                                     (launchBudgetEvent == null || evt.CreatedUtc > launchBudgetEvent.CreatedUtc))
                            {
                                launchBudgetEvent = evt;
                            }
                        }
                        if (packEvent != null)
                            mission.ContextPackUsage = ContextPackUsageSummary.FromEventPayload(packEvent.Payload);
                        if (budgetEvent != null)
                            mission.PromptBudget = PromptBudgetSummary.FromEventPayloads(
                                budgetEvent.Payload, launchBudgetEvent?.Payload);
                    }
                    catch (Exception)
                    {
                        // Non-fatal; leave ContextPackUsage and PromptBudget null.
                    }
                    return (object)mission;
                });

            register(
                "armada_mission_output",
                "Read a digest-backed page of the authoritative safe output artifact for a mission. Secret-shaped values are redacted. Continue at nextOffset until hasMore is false, then verify sha256 and complete before treating the report as complete.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission ID (msn_ prefix)" },
                        offset = new { type = "integer", description = "Zero-based character offset (default 0)" },
                        length = new { type = "integer", description = "Characters to return (default 16000, maximum 64000)" }
                    },
                    required = new[] { "missionId" }
                },
                async (args) =>
                {
                    MissionOutputArgs request = JsonSerializer.Deserialize<MissionOutputArgs>(args!.Value, _JsonOptions)!;
                    Mission? mission = await database.Missions.ReadAsync(
                        ArmadaConstants.DefaultTenantId,
                        request.MissionId).ConfigureAwait(false);
                    if (mission == null) return (object)new { Error = "Mission not found" };
                    try
                    {
                        return MissionOutputArtifact.Build(mission, request.Offset, request.Length);
                    }
                    catch (ArgumentOutOfRangeException ex)
                    {
                        return (object)new { Error = ex.ParamName + " is outside the valid output page range" };
                    }
                });

            register(
                "armada_create_mission",
                "Create and dispatch a standalone mission to a vessel",
                new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string", description = "Mission title" },
                        description = new { type = "string", description = "Mission description/instructions" },
                        vesselId = new { type = "string", description = "Target vessel ID (vsl_ prefix)" },
                        voyageId = new { type = "string", description = "Optional voyage ID to associate with (vyg_ prefix)" },
                        persona = new { type = "string", description = "Persona for this mission (e.g. Worker, Architect, Judge, TestEngineer)" },
                        mode = new { type = "string", description = "Optional mission mode: Implementation (default), Audit, or Research. Implementation changes code and must commit. Audit and Research deliver a report: they receive a reduced instruction set and producing no commit is their success condition, not a failure. An unrecognized value is rejected." },
                        dependsOnMissionId = new { type = "string", description = "Optional mission ID (msn_ prefix) this mission must wait for. The dependent mission stays Pending until the referenced mission reaches a completion state." },
                        selectedPlaybooks = new
                        {
                            type = "array",
                            description = "Ordered playbooks to apply during mission dispatch",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    playbookId = new { type = "string", description = "Playbook ID (pbk_ prefix)" },
                                    deliveryMode = new { type = "string", description = "InlineFullContent, InstructionWithReference, or AttachIntoWorktree" }
                                },
                                required = new[] { "playbookId", "deliveryMode" }
                            }
                        }
                    },
                    required = new[] { "title", "description", "vesselId" }
                },
                async (args) =>
                {
                    MissionCreateArgs request = JsonSerializer.Deserialize<MissionCreateArgs>(args!.Value, _JsonOptions)!;
                    // The mission is owned by the authenticated caller, exactly as a REST create is.
                    AuthContext createCaller = McpCallerContext.Require();
                    Mission mission = new Mission();
                    mission.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(createCaller);
                    mission.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(createCaller);
                    mission.Title = request.Title;
                    mission.Description = request.Description;
                    mission.VesselId = request.VesselId;
                    if (request.VoyageId != null)
                        mission.VoyageId = request.VoyageId;
                    mission.Persona = request.Persona;

                    // Reject an unrecognized mode instead of parsing it down to Implementation: a typo
                    // would otherwise produce an implementing mission judged by the commit gate, which
                    // is the failure mode modes exist to remove.
                    if (!String.IsNullOrWhiteSpace(request.Mode) && !Armada.Core.Enums.MissionModes.IsKnown(request.Mode))
                        return (object)new
                        {
                            Error = "Unknown mission mode: " + request.Mode + ". Use Implementation, Audit, or Research, or omit mode to default to Implementation."
                        };
                    mission.Mode = Armada.Core.Enums.MissionModes.Parse(request.Mode);

                    if (!String.IsNullOrEmpty(request.DependsOnMissionId))
                    {
                        Mission? referenced = await database.Missions.ReadAsync(request.DependsOnMissionId).ConfigureAwait(false);
                        if (referenced == null)
                            return (object)new { Error = "dependsOnMissionId not found: " + request.DependsOnMissionId };
                        mission.DependsOnMissionId = request.DependsOnMissionId;
                    }
                    mission.SelectedPlaybooks = request.SelectedPlaybooks ?? new List<SelectedPlaybook>();
                    string? unreachable = await MissionReferenceScope.FindUnreachableOnCreateAsync(database, createCaller, mission).ConfigureAwait(false);
                    if (unreachable != null) return (object)new { Error = unreachable };
                    await MissionDefaultPlaybooks.MergeVesselDefaultsAsync(database, mission).ConfigureAwait(false);
                    try
                    {
                        mission = await admiral.DispatchMissionAsync(mission).ConfigureAwait(false);
                    }
                    catch (DispatchHoldActiveException held)
                    {
                        return (object)DispatchHoldRefusal.From(held.Hold);
                    }
                    catch (FleetCapacityAdmissionException capacity)
                    {
                        return (object)new
                        {
                            Error = capacity.Message,
                            capacity.Code,
                            capacity.ActiveCount,
                            capacity.Limit,
                            capacity.CandidateVesselId,
                            capacity.LaneMembers
                        };
                    }
                    if (mission.Status == Armada.Core.Enums.MissionStatusEnum.Pending)
                    {
                        return (object)new
                        {
                            Mission = SanitizeMissionForStatus(mission),
                            Warning = "Mission created but could not be assigned to any captain. It will be retried on the next health check cycle."
                        };
                    }
                    return (object)SanitizeMissionForStatus(mission);
                });

            register(
                "armada_update_mission",
                "Update an existing mission's title, description, priority, branch, or PR URL. A mission cannot move to another vessel or voyage. Operational fields (status, timestamps, captain) are managed by the system.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission ID (msn_ prefix)" },
                        title = new { type = "string", description = "New mission title" },
                        description = new { type = "string", description = "New mission description/instructions" },
                        vesselId = new { type = "string", description = "The mission's current vessel ID (vsl_ prefix). A different vessel is refused; a mission cannot be moved." },
                        voyageId = new { type = "string", description = "The mission's current voyage ID (vyg_ prefix). A different voyage is refused; a mission cannot be moved." },
                        priority = new { type = "integer", description = "New priority (lower is higher priority)" },
                        branchName = new { type = "string", description = "Git branch name for this mission" },
                        prUrl = new { type = "string", description = "Pull request URL" },
                        parentMissionId = new { type = "string", description = "Parent mission ID for sub-tasks (msn_ prefix)" },
                        persona = new { type = "string", description = "Persona for this mission (e.g. Worker, Architect, Judge, TestEngineer)" },
                        dependsOnMissionId = new { type = "string", description = "Mission ID (msn_ prefix) this mission must wait for. Pass an empty string to clear the dependency." }
                    },
                    required = new[] { "missionId" }
                },
                async (args) =>
                {
                    MissionUpdateArgs request = JsonSerializer.Deserialize<MissionUpdateArgs>(args!.Value, _JsonOptions)!;
                    string missionId = request.MissionId;
                    // The mission and every mission it is linked to are read in the caller's scope, exactly as the
                    // REST update reads them.
                    AuthContext updateCaller = McpCallerContext.Require();
                    Mission? mission = await Armada.Core.Authorization.CallerScopedRead.ReadMissionAsync(database, updateCaller, missionId).ConfigureAwait(false);
                    if (mission == null) return (object)new { Error = "Mission not found" };
                    string? bindingError = MissionMetadataUpdate.CheckBindings(mission, request.VesselId != null, request.VesselId, request.VoyageId != null, request.VoyageId);
                    if (bindingError != null) return (object)new { Error = bindingError };
                    if (!String.IsNullOrEmpty(request.ParentMissionId)
                        && !String.Equals(request.ParentMissionId, mission.ParentMissionId, StringComparison.Ordinal)
                        && await Armada.Core.Authorization.CallerScopedRead.ReadMissionAsync(database, updateCaller, request.ParentMissionId).ConfigureAwait(false) == null)
                        return (object)new { Error = "parentMissionId not found: " + request.ParentMissionId };
                    if (request.Title != null)
                        mission.Title = request.Title;
                    if (request.Description != null)
                        mission.Description = request.Description;
                    if (request.Priority.HasValue)
                        mission.Priority = request.Priority.Value;
                    if (request.BranchName != null)
                        mission.BranchName = request.BranchName;
                    if (request.PrUrl != null)
                        mission.PrUrl = request.PrUrl;
                    if (request.ParentMissionId != null)
                        mission.ParentMissionId = request.ParentMissionId;
                    if (request.Persona != null)
                        mission.Persona = request.Persona;
                    if (request.DependsOnMissionId != null)
                    {
                        if (request.DependsOnMissionId.Length == 0)
                        {
                            mission.DependsOnMissionId = null;
                        }
                        else
                        {
                            Mission? referenced = await Armada.Core.Authorization.CallerScopedRead.ReadMissionAsync(database, updateCaller, request.DependsOnMissionId).ConfigureAwait(false);
                            if (referenced == null)
                                return (object)new { Error = "dependsOnMissionId not found: " + request.DependsOnMissionId };
                            mission.DependsOnMissionId = request.DependsOnMissionId;
                        }
                    }
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    mission = await database.Missions.UpdateAsync(mission).ConfigureAwait(false);
                    return (object)SanitizeMissionForStatus(mission);
                });

            register(
                "armada_cancel_mission",
                "Cancel a specific mission",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission ID (msn_ prefix)" }
                    },
                    required = new[] { "missionId" }
                },
                async (args) =>
                {
                    MissionIdArgs request = JsonSerializer.Deserialize<MissionIdArgs>(args!.Value, _JsonOptions)!;
                    string missionId = request.MissionId;
                    Mission? mission = await database.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    if (mission == null) return (object)new { Error = "Mission not found" };

                    // Kill the running agent process if this mission's captain is currently
                    // executing it. Without this, in-flight cancels leave the captain stuck
                    // Working forever and the dispatcher refuses to assign new missions to
                    // that captain or single-captain pool.
                    if (!String.IsNullOrEmpty(mission.CaptainId))
                    {
                        Captain? captain = await database.Captains.ReadAsync(mission.CaptainId).ConfigureAwait(false);
                        if (captain != null && captain.CurrentMissionId == mission.Id)
                        {
                            List<Mission> otherMissions = (await database.Missions.EnumerateByCaptainAsync(captain.Id).ConfigureAwait(false))
                                .Where(m => m.Id != mission.Id && (m.Status == MissionStatusEnum.InProgress || m.Status == MissionStatusEnum.Assigned)).ToList();
                            if (otherMissions.Count == 0)
                            {
                                if (onStopCaptain != null)
                                {
                                    try { await onStopCaptain(captain.Id).ConfigureAwait(false); }
                                    catch { /* best-effort; still reset DB state */ }
                                }
                                try { await admiral.RecallCaptainAsync(captain.Id).ConfigureAwait(false); }
                                catch
                                {
                                    captain.State = CaptainStateEnum.Idle;
                                    captain.CurrentMissionId = null;
                                    captain.CurrentDockId = null;
                                    captain.ProcessId = null;
                                    captain.RecoveryAttempts = 0;
                                    captain.LastUpdateUtc = DateTime.UtcNow;
                                    await database.Captains.UpdateAsync(captain).ConfigureAwait(false);
                                }
                            }
                        }
                    }

                    mission.Status = MissionStatusEnum.Cancelled;
                    mission.ProcessId = null;
                    mission.CompletedUtc = DateTime.UtcNow;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    mission = await database.Missions.UpdateAsync(mission).ConfigureAwait(false);
                    return (object)SanitizeMissionForStatus(mission);
                });

            register(
                "armada_purge_mission",
                "Permanently delete a mission from the database. This cannot be undone.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission ID (msn_ prefix)" }
                    },
                    required = new[] { "missionId" }
                },
                async (args) =>
                {
                    MissionIdArgs request = JsonSerializer.Deserialize<MissionIdArgs>(args!.Value, _JsonOptions)!;
                    string missionId = request.MissionId;
                    Mission? mission = await database.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    if (mission == null) return (object)new { Error = "Mission not found" };

                    // Clean up associated dock/worktree if present
                    if (!String.IsNullOrEmpty(mission.DockId))
                    {
                        try
                        {
                            Dock? dock = await database.Docks.ReadAsync(mission.DockId).ConfigureAwait(false);
                            if (dock != null)
                            {
                                if (!String.IsNullOrEmpty(dock.WorktreePath) && Directory.Exists(dock.WorktreePath))
                                {
                                    try { Directory.Delete(dock.WorktreePath, true); }
                                    catch { }
                                }
                                await database.Docks.DeleteAsync(dock.Id).ConfigureAwait(false);
                            }
                        }
                        catch { }
                    }

                    // Clean up log files if settings are available
                    if (settings != null)
                    {
                        try
                        {
                            string logPath = Path.Combine(settings.LogDirectory, "missions", missionId + ".log");
                            if (File.Exists(logPath)) File.Delete(logPath);
                        }
                        catch { }
                        try
                        {
                            string diffPath = Path.Combine(settings.LogDirectory, "diffs", missionId + ".diff");
                            if (File.Exists(diffPath)) File.Delete(diffPath);
                        }
                        catch { }
                    }

                    await database.Missions.DeleteAsync(missionId).ConfigureAwait(false);
                    return (object)new { Status = "deleted", MissionId = missionId };
                });

            register(
                "armada_delete_missions",
                "Permanently delete multiple missions from the database by ID. Returns a summary of deleted and skipped entries. This cannot be undone.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        ids = new { type = "array", items = new { type = "string" }, description = "List of mission IDs to delete (msn_ prefix)" }
                    },
                    required = new[] { "ids" }
                },
                async (args) =>
                {
                    DeleteMultipleArgs request = JsonSerializer.Deserialize<DeleteMultipleArgs>(args!.Value, _JsonOptions)!;
                    if (request.Ids == null || request.Ids.Count == 0)
                        return (object)new { Error = "ids is required and must not be empty" };

                    DeleteMultipleResult result = new DeleteMultipleResult();
                    foreach (string id in request.Ids)
                    {
                        if (String.IsNullOrEmpty(id))
                        {
                            result.Skipped.Add(new DeleteMultipleSkipped(id ?? "", "Empty ID"));
                            continue;
                        }
                        Mission? mission = await database.Missions.ReadAsync(id).ConfigureAwait(false);
                        if (mission == null)
                        {
                            result.Skipped.Add(new DeleteMultipleSkipped(id, "Not found"));
                            continue;
                        }
                        // Clean up associated dock/worktree if present
                        if (!String.IsNullOrEmpty(mission.DockId))
                        {
                            try
                            {
                                Dock? dock = await database.Docks.ReadAsync(mission.DockId).ConfigureAwait(false);
                                if (dock != null)
                                {
                                    if (!String.IsNullOrEmpty(dock.WorktreePath) && Directory.Exists(dock.WorktreePath))
                                    {
                                        try { Directory.Delete(dock.WorktreePath, true); }
                                        catch { }
                                    }
                                    await database.Docks.DeleteAsync(dock.Id).ConfigureAwait(false);
                                }
                            }
                            catch { }
                        }

                        // Clean up log files if settings are available
                        if (settings != null)
                        {
                            try
                            {
                                string logPath = Path.Combine(settings.LogDirectory, "missions", id + ".log");
                                if (File.Exists(logPath)) File.Delete(logPath);
                            }
                            catch { }
                            try
                            {
                                string diffPath = Path.Combine(settings.LogDirectory, "diffs", id + ".diff");
                                if (File.Exists(diffPath)) File.Delete(diffPath);
                            }
                            catch { }
                        }

                        await database.Missions.DeleteAsync(id).ConfigureAwait(false);
                        result.Deleted++;
                    }
                    result.ResolveStatus();
                    return (object)result;
                });

            register(
                "armada_restart_mission",
                "Restart a failed or cancelled mission, resetting it to Pending for re-dispatch. Optionally update title and description (instructions) before restarting.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission ID (msn_ prefix)" },
                        title = new { type = "string", description = "Optional new title. Omit to keep original." },
                        description = new { type = "string", description = "Optional new description/instructions. Omit to keep original." }
                    },
                    required = new[] { "missionId" }
                },
                async (args) =>
                {
                    MissionRestartArgs request = JsonSerializer.Deserialize<MissionRestartArgs>(args!.Value, _JsonOptions)!;
                    string missionId = request.MissionId;
                    Mission? mission = await database.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    if (mission == null) return (object)new { Error = "Mission not found" };

                    if (mission.Status != MissionStatusEnum.Failed && mission.Status != MissionStatusEnum.Cancelled)
                        return (object)new { Error = "Only Failed or Cancelled missions can be restarted (current: " + mission.Status + ")" };

                    if (!String.IsNullOrEmpty(request.Title)) mission.Title = request.Title;
                    if (!String.IsNullOrEmpty(request.Description)) mission.Description = request.Description;

                    try
                    {
                        MissionRestartService restarts = new MissionRestartService(database, settings ?? new ArmadaSettings());
                        mission = await restarts.RestartAsync(mission, mission.Title, mission.Description).ConfigureAwait(false);
                    }
                    catch (FleetCapacityAdmissionException capacity)
                    {
                        return (object)new
                        {
                            Error = capacity.Message,
                            capacity.Code,
                            capacity.ActiveCount,
                            capacity.Limit,
                            capacity.CandidateVesselId,
                            capacity.LaneMembers
                        };
                    }

                    // The restart signal belongs to the mission it reports, so the mission's owner sees it.
                    Signal signal = new Signal(SignalTypeEnum.Progress, "Mission " + missionId + " restarted");
                    signal.TenantId = mission.TenantId;
                    signal.UserId = mission.UserId;
                    await database.Signals.CreateAsync(signal).ConfigureAwait(false);

                    return (object)SanitizeMissionForStatus(mission);
                });

            register(
                "armada_retry_landing",
                "Retry landing for a mission in LandingFailed status. Rebases the mission branch onto the current target and re-attempts landing.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission ID (msn_ prefix) to retry landing for" }
                    },
                    required = new[] { "missionId" }
                },
                async (args) =>
                {
                    if (landingService == null) return (object)new { Error = "Landing service not configured" };
                    MissionRetryLandingArgs request = JsonSerializer.Deserialize<MissionRetryLandingArgs>(args!.Value, _JsonOptions)!;
                    bool success = await landingService.RetryLandingAsync(request.MissionId).ConfigureAwait(false);
                    Mission? mission = await database.Missions.ReadSummaryAsync(request.MissionId).ConfigureAwait(false);
                    return (object)new { Success = success, Mission = mission };
                });

            register(
                "armada_transition_mission_status",
                "Transition a mission to a new status with validation. Valid transitions: Pending->Assigned, Assigned->InProgress, InProgress->Testing/Review/Complete/Failed, Testing->Review/InProgress/Complete/Failed, Review->Complete/InProgress/Failed. Most states allow ->Cancelled.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission ID (msn_ prefix)" },
                        status = new { type = "string", description = "Target status: Pending, Assigned, InProgress, WorkProduced, Testing, Review, Complete, Failed, LandingFailed, Cancelled" }
                    },
                    required = new[] { "missionId", "status" }
                },
                async (args) =>
                {
                    MissionTransitionArgs request = JsonSerializer.Deserialize<MissionTransitionArgs>(args!.Value, _JsonOptions)!;
                    string missionId = request.MissionId;
                    string statusStr = request.Status;

                    // The transition runs as the authenticated caller. A mission the caller may not
                    // read is reported as missing, so its existence is not disclosed across owners.
                    AuthContext caller = McpCallerContext.Require();
                    Mission? mission = await database.Missions.ReadAsync(missionId).ConfigureAwait(false);
                    if (mission == null
                        || !Armada.Core.Authorization.OwnershipPolicy.CanView(caller, mission.TenantId, mission.UserId, OwnershipScopeEnum.UserSpecific))
                        return (object)new { Error = "Mission not found" };

                    if (!Enum.TryParse<MissionStatusEnum>(statusStr, true, out MissionStatusEnum newStatus))
                        return (object)new { Error = "Invalid status: " + statusStr };

                    if (statusTransitions == null)
                        return (object)new { Error = MissionStatusTransitionService.UnavailableMessage };

                    // The shared operator transition path applies the same validation, manual
                    // completion gates, landing, and handoff as the REST status route.
                    MissionStatusTransitionResult transition = await statusTransitions.TransitionAsync(mission, newStatus).ConfigureAwait(false);
                    if (transition.Outcome != MissionStatusTransitionOutcomeEnum.Applied)
                        return (object)new { Error = transition.Message, Reason = transition.Reason };

                    return (object)SanitizeMissionForStatus(transition.Mission!);
                });

            // Diff and log tools require settings and git service
            if (settings != null)
            {
                register(
                    "armada_get_mission_diff",
                    "Get the git diff of changes made by a captain for a mission. Returns saved diff if available, otherwise live worktree diff.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            missionId = new { type = "string", description = "Mission ID (msn_ prefix)" }
                        },
                        required = new[] { "missionId" }
                    },
                    async (args) =>
                    {
                        MissionIdArgs request = JsonSerializer.Deserialize<MissionIdArgs>(args!.Value, _JsonOptions)!;
                        string missionId = request.MissionId;
                        Mission? mission = await database.Missions.ReadSummaryAsync(missionId).ConfigureAwait(false);
                        if (mission == null) return (object)new { Error = "Mission not found" };

                        // Check for a saved diff file first
                        string savedDiffPath = Path.Combine(settings.LogDirectory, "diffs", missionId + ".diff");
                        if (File.Exists(savedDiffPath))
                        {
                            string savedDiff = await McpToolHelpers.ReadTextFileSafeAsync(savedDiffPath).ConfigureAwait(false);
                            return (object)new { MissionId = missionId, Branch = mission.BranchName ?? "", Diff = savedDiff };
                        }

                        // Check for database-persisted diff snapshot
                        if (!String.IsNullOrEmpty(mission.DiffSnapshot))
                        {
                            return (object)new { MissionId = missionId, Branch = mission.BranchName ?? "", Diff = mission.DiffSnapshot };
                        }

                        // Fall back to live worktree diff
                        if (git == null)
                            return (object)new { Error = "No saved diff available and git service not configured" };

                        Dock? dock = null;
                        if (!String.IsNullOrEmpty(mission.DockId))
                        {
                            dock = await database.Docks.ReadAsync(mission.DockId).ConfigureAwait(false);
                        }

                        if (dock == null && !String.IsNullOrEmpty(mission.CaptainId))
                        {
                            Captain? captain = await database.Captains.ReadAsync(mission.CaptainId).ConfigureAwait(false);
                            if (captain != null && !String.IsNullOrEmpty(captain.CurrentDockId))
                                dock = await database.Docks.ReadAsync(captain.CurrentDockId).ConfigureAwait(false);
                        }

                        if (dock == null && !String.IsNullOrEmpty(mission.BranchName) && !String.IsNullOrEmpty(mission.VesselId))
                        {
                            List<Dock> docks = await database.Docks.EnumerateByVesselAsync(mission.VesselId).ConfigureAwait(false);
                            dock = docks.FirstOrDefault(d => d.BranchName == mission.BranchName && d.Active);
                        }

                        if (dock == null || String.IsNullOrEmpty(dock.WorktreePath) || !Directory.Exists(dock.WorktreePath))
                            return (object)new { Error = "No diff available — worktree was already reclaimed and no saved diff exists" };

                        string baseBranch = "main";
                        if (!String.IsNullOrEmpty(mission.VesselId))
                        {
                            Vessel? vessel = await database.Vessels.ReadAsync(mission.VesselId).ConfigureAwait(false);
                            if (vessel != null) baseBranch = vessel.DefaultBranch;
                        }

                        string diff = await git.DiffAsync(dock.WorktreePath, baseBranch).ConfigureAwait(false);
                        return (object)new { MissionId = missionId, Branch = dock.BranchName ?? "", Diff = diff };
                    });

                register(
                    "armada_get_mission_log",
                    "Get the session log for a mission. Supports pagination.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            missionId = new { type = "string", description = "Mission ID (msn_ prefix)" },
                            lines = new { type = "integer", description = "Number of lines to return (default 100)" },
                            offset = new { type = "integer", description = "Line offset to start from (default 0)" }
                        },
                        required = new[] { "missionId" }
                    },
                    async (args) =>
                    {
                        MissionLogArgs request = JsonSerializer.Deserialize<MissionLogArgs>(args!.Value, _JsonOptions)!;
                        string missionId = request.MissionId;
                        Mission? mission = await database.Missions.ReadAsync(missionId).ConfigureAwait(false);
                        if (mission == null) return (object)new { Error = "Mission not found" };

                        string logPath = Path.Combine(settings.LogDirectory, "missions", missionId + ".log");
                        if (!File.Exists(logPath))
                            return (object)new { MissionId = missionId, Log = "", Lines = 0, TotalLines = 0 };

                        string[] allLines = RuntimeLogNoiseFilter.Filter(
                            await McpToolHelpers.ReadLogFileSafeAsync(logPath).ConfigureAwait(false));
                        int totalLines = allLines.Length;

                        int offset = Math.Max(0, request.Offset ?? 0);
                        int lineCount = Math.Max(1, request.Lines ?? 100);

                        string[] slice = allLines.Skip(offset).Take(lineCount).ToArray();
                        string log = Armada.Core.Services.RuntimeLogFormatter.RedactSecrets(String.Join("\n", slice));
                        return (object)new { MissionId = missionId, Log = log, Lines = slice.Length, TotalLines = totalLines };
                    });
            }
        }

        private static Mission SanitizeMissionForStatus(Mission mission)
        {
            mission.DiffSnapshot = null;
            mission.AgentOutput = null;
            mission.PlaybookSnapshots = new List<MissionPlaybookSnapshot>();
            return mission;
        }
    }
}
