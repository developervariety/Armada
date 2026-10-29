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
        /// <summary>Error returned when a mission read names no mission and the caller holds no mission credential.</summary>
        internal const string MissingMissionIdError = "missionId is required: pass a mission id (msn_ prefix). A captain may omit it to read its own mission.";

        /// <summary>Job operation name of a landing retry; at most one runs per mission.</summary>
        public const string RetryLandingJobOperation = "mission_retry_landing";

        /// <summary>Refusal code when a landing retry for the mission is already running outside the job registry.</summary>
        public const string RetryLandingInFlightCode = "retry_landing_in_flight";

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
        /// <param name="statusTransitions">Shared operator status transition path used by
        /// armada_transition_mission_status; without it every transition is refused.</param>
        /// <param name="operations">Shared mission operations (cancel, purge, restart) REST and WebSocket use. When
        /// null, one is built from <paramref name="database"/>, <paramref name="settings"/> and <paramref name="admiral"/>
        /// that removes no docks and writes no events.</param>
        /// <param name="jobs">Optional long-running job registry. When supplied, armada_retry_landing returns an
        /// accepted job handle at once instead of holding the caller for the length of the landing.</param>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            IAdmiralService admiral,
            ArmadaSettings? settings,
            IGitService? git,
            ILandingService? landingService = null,
            MissionStatusTransitionService? statusTransitions = null,
            MissionOperations? operations = null,
            LongRunningJobService? jobs = null)
        {
            MissionOperations missionOperations = operations ?? new MissionOperations(
                database,
                settings ?? new ArmadaSettings(),
                null,
                (captainId, token) => admiral.RecallCaptainAsync(captainId, token),
                OperationNotifier.None);

            if (settings != null && git != null)
                McpResearchReplayTools.Register(register, database, admiral, settings, git, missionOperations.Notifier);

            register(
                "armada_mission_status",
                "Get status of a specific mission",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission ID (msn_ prefix). A captain may omit it to read its own mission." },
                        includeDescription = new { type = "boolean", description = "Return the stored mission description (the full brief text). Default false." }
                    },
                    required = Array.Empty<string>()
                },
                async (args) =>
                {
                    MissionStatusArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<MissionStatusArgs>(args.Value, _JsonOptions) ?? new MissionStatusArgs()
                        : new MissionStatusArgs();
                    string? resolvedMissionId = McpCallerContext.MissionIdOrOwn(request.MissionId);
                    if (resolvedMissionId == null) return (object)new { Error = MissingMissionIdError };
                    string missionId = resolvedMissionId;
                    Mission? authorized = await McpMissionEvidenceScope.ReadMissionAsync(
                        database, McpCallerContext.Require(), missionId).ConfigureAwait(false);
                    if (authorized == null)
                        return (object)await McpMissionEvidenceScope.DenyMissionAsync(database, McpCallerContext.Require(), missionId).ConfigureAwait(false);
                    missionId = authorized.Id;

                    Mission? mission = await database.Missions.ReadSummaryAsync(missionId).ConfigureAwait(false);
                    if (mission == null) return (object)new { Error = "Mission not found" };
                    if (request.IncludeDescription)
                    {
                        Mission? full = await database.Missions.ReadAsync(missionId).ConfigureAwait(false);
                        mission.Description = full?.Description;
                    }
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
                    return (object)SanitizeMissionForStatus(mission);
                });

            register(
                "armada_mission_output",
                "Read a digest-backed page of the authoritative safe output artifact for a mission. Mission callers may read their own mission, same-voyage reports, and direct dependencies or parents within the same owner and tenant. A later stage in an autonomous rescue may also read the failed mission named by that rescue chain's root. Secret-shaped values are redacted. Continue at NextOffset until HasMore is false, then verify Sha256 and Complete before treating the report as complete.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        missionId = new { type = "string", description = "Mission ID (msn_ prefix). A captain may omit it to read its own mission." },
                        offset = new { type = "integer", description = "Zero-based character offset (default 0)" },
                        length = new { type = "integer", description = "Characters to return (default 16000, maximum 64000)" }
                    },
                    required = Array.Empty<string>()
                },
                async (args) =>
                {
                    MissionOutputArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<MissionOutputArgs>(args.Value, _JsonOptions) ?? new MissionOutputArgs()
                        : new MissionOutputArgs();
                    string? outputMissionId = McpCallerContext.MissionIdOrOwn(request.MissionId);
                    if (outputMissionId == null) return (object)new { Error = MissingMissionIdError };
                    Mission? mission = await McpMissionEvidenceScope.ReadMissionAsync(
                        database, McpCallerContext.Require(), outputMissionId).ConfigureAwait(false);
                    if (mission == null)
                        return (object)await McpMissionEvidenceScope.DenyMissionAsync(database, McpCallerContext.Require(), outputMissionId).ConfigureAwait(false);
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
                        tier = new { type = "string", description = "Optional minimum captain tier for this mission (Economy, Standard, or Premium). Only captains at or above it are assigned. Omit it to keep the default routing. An unrecognized value is rejected." },
                        dependsOnMissionId = new { type = "string", description = "Optional mission ID (msn_ prefix) this mission must wait for. The dependent mission stays Pending until the referenced mission reaches a completion state." },
                        isEvaluation = new { type = "boolean", description = "When true, the mission is evaluation work and stays out of production summaries. The default is false. A mission added to an evaluation voyage inherits that voyage's flag." },
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
                    mission.IsEvaluation = request.IsEvaluation;

                    if (!String.IsNullOrWhiteSpace(request.Tier))
                    {
                        if (!Enum.TryParse<CaptainTierEnum>(request.Tier.Trim(), true, out CaptainTierEnum missionTier) || !Enum.IsDefined(missionTier))
                        {
                            return (object)new
                            {
                                Error = "Unknown captain tier: " + request.Tier + ". Use Economy, Standard, or Premium, or omit tier."
                            };
                        }
                        mission.Tier = missionTier;
                    }

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
                    // The shared metadata update REST and WebSocket use: only named fields change, the vessel and voyage
                    // cannot change, and a changed link must name a mission visible to the caller.
                    MissionMetadataPatch patch = new MissionMetadataPatch
                    {
                        Title = request.Title,
                        Description = request.Description,
                        Priority = request.Priority,
                        BranchName = request.BranchName,
                        PrUrl = request.PrUrl,
                        ParentMissionId = request.ParentMissionId,
                        DependsOnMissionId = request.DependsOnMissionId,
                        Persona = request.Persona,
                        HasVesselId = request.VesselId != null,
                        VesselId = request.VesselId,
                        HasVoyageId = request.VoyageId != null,
                        VoyageId = request.VoyageId
                    };
                    if (request.Title != null) patch.Named.Add("title");
                    if (request.Description != null) patch.Named.Add("description");
                    if (request.Priority.HasValue) patch.Named.Add("priority");
                    if (request.BranchName != null) patch.Named.Add("branchName");
                    if (request.PrUrl != null) patch.Named.Add("prUrl");
                    if (request.ParentMissionId != null) patch.Named.Add("parentMissionId");
                    if (request.DependsOnMissionId != null) patch.Named.Add("dependsOnMissionId");
                    if (request.Persona != null) patch.Named.Add("persona");
                    MissionUpdateResult update = await missionOperations.UpdateMissionMetadataAsync(mission, patch, McpCallerContext.Require()).ConfigureAwait(false);
                    if (!update.Succeeded) return (object)new { Error = update.Message, Code = update.Code };
                    return (object)SanitizeMissionForStatus(update.Mission);
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

                    // REST, WebSocket and MCP share one mission cancel: the finished-mission refusal, the captain
                    // recall (which stops the agent process), the dependent-stage cascade, the event and the broadcast.
                    MissionCancellationResult cancellation = await missionOperations.CancelMissionAsync(mission).ConfigureAwait(false);
                    if (!cancellation.Succeeded)
                        return (object)new { Error = cancellation.Message, Code = cancellation.Code };
                    return (object)SanitizeMissionForStatus(cancellation.Mission);
                });

            register(
                "armada_purge_mission",
                "Permanently delete a mission with its dock record, worktree, log files and saved diff. A mission a captain is working is refused. This cannot be undone.",
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

                    WorkPurgeResult purge = await missionOperations.PurgeMissionAsync(mission).ConfigureAwait(false);
                    if (!purge.Succeeded)
                        return (object)new { Error = purge.Message, Code = purge.Code };
                    return (object)new { Status = "deleted", MissionId = missionId };
                });

            register(
                "armada_delete_missions",
                "Permanently delete multiple missions by ID, each by the single-mission purge rule; a mission a captain is working is skipped with its reason. Returns a summary of deleted and skipped entries. This cannot be undone.",
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

                    DeleteMultipleResult result = await missionOperations.PurgeMissionsAsync(
                        request.Ids,
                        id => database.Missions.ReadAsync(id)).ConfigureAwait(false);
                    return (object)result;
                });

            register(
                "armada_restart_mission",
                "Restart a Failed or Cancelled mission, resetting it to Pending for re-dispatch. Optionally update title and description (instructions) before restarting. A LandingFailed mission keeps its produced work and is refused: use armada_retry_landing. A mission whose voyage is Complete or Cancelled is refused. An eligible failed stage, or a dependent stage cancelled after it, can reopen a Failed voyage at the nearest verified upstream commit, or its own start when none exists; a stage an operator stopped reopens a Failed or Cancelled voyage the same way; cancelled downstream stages also return to Pending. Other ended-voyage cases require a new voyage or a rescue.",
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

                    // REST, WebSocket and MCP share one restart: the eligibility rule (LandingFailed is refused with a
                    // pointer to retry-landing; a mission of an ended voyage is refused), the capacity gate, the owned signal, the event
                    // and the broadcast.
                    MissionRestartResult restart;
                    try
                    {
                        restart = await missionOperations.RestartMissionAsync(mission, request.Title, request.Description).ConfigureAwait(false);
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

                    if (!restart.Succeeded)
                        return (object)new { Error = restart.Message, Code = restart.Code };
                    return (object)SanitizeMissionForStatus(restart.Mission);
                });

            register(
                "armada_retry_landing",
                jobs == null
                    ? "Retry landing for a mission in WorkProduced or LandingFailed status. Rebases the mission branch onto the current target and re-attempts landing."
                    : "Start a landing retry for a mission in WorkProduced or LandingFailed status (rebase the mission branch onto the current target and land) and "
                        + "immediately return an accepted job handle. Use armada_job_status to read the outcome. A retry requested while one runs for the same "
                        + "mission returns the running job (Joined=true) and never lands the work twice.",
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
                    string missionId = request.MissionId;

                    if (jobs != null)
                    {
                        // A landing can outlast any client deadline, and a caller that lost the reply would retry a
                        // landing that is still running. The retry runs as a job, and a repeated request joins it.
                        LongRunningJob? running = jobs.FindUnfinished(RetryLandingJobOperation, missionId);
                        if (running != null) return ShapeRetryLandingJob(running, true);

                        Mission? target = await database.Missions.ReadAsync(missionId).ConfigureAwait(false);
                        if (target == null) return (object)new { Error = "Mission not found", Code = "mission_not_found" };
                        if (landingService.IsRetryInFlight(missionId))
                            return (object)new { Error = LandingService.RetryInFlightMessage(missionId), Code = RetryLandingInFlightCode };
                        if (target.Status != MissionStatusEnum.WorkProduced && target.Status != MissionStatusEnum.LandingFailed)
                        {
                            return (object)new
                            {
                                Error = "Only WorkProduced or LandingFailed missions can retry landing (current: " + target.Status + ")",
                                Code = "mission_not_landable"
                            };
                        }

                        LongRunningJob job = jobs.StartOrJoin(
                            RetryLandingJobOperation,
                            missionId,
                            async (token) =>
                            {
                                bool landed = await landingService.RetryLandingAsync(missionId, null, token).ConfigureAwait(false);
                                Mission? after = await database.Missions.ReadSummaryAsync(missionId, token).ConfigureAwait(false);
                                if (!landed)
                                {
                                    throw new InvalidOperationException("retry_landing_failed: the mission did not land (status "
                                        + (after?.Status.ToString() ?? "unknown") + "). " + (after?.FailureReason ?? String.Empty));
                                }
                                return (object?)new { Success = true, Mission = after };
                            },
                            out bool joined,
                            null,
                            target.VesselId);
                        return ShapeRetryLandingJob(job, joined);
                    }

                    bool success = await landingService.RetryLandingAsync(missionId).ConfigureAwait(false);
                    Mission? mission = await database.Missions.ReadSummaryAsync(missionId).ConfigureAwait(false);
                    return (object)new { Success = success, Mission = mission };
                });

            register(
                "armada_transition_mission_status",
                "Transition a mission to a new status with validation. Valid transitions: Pending->Assigned, Assigned->InProgress, InProgress->Testing/Review/Complete/Failed, Testing->Review/InProgress/Complete/Failed, Review->Complete/InProgress/Failed. Most states allow ->Cancelled. LandingFailed->Complete is accepted only to confirm a landing proposal.",
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
                    "Get the git diff of changes made by a captain for a mission: the saved diff file, then the diff snapshot stored on the mission, then the live worktree diff.",
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

                        // REST, WebSocket and MCP read a diff through one reader: the saved diff file, then the stored
                        // snapshot, then the live worktree.
                        MissionDiffResult diff = await MissionDiffReader.ReadAsync(database, settings.LogDirectory, git, mission).ConfigureAwait(false);
                        if (!diff.Available) return (object)new { Error = diff.Message };
                        return (object)new { MissionId = missionId, Branch = diff.Branch, Diff = diff.Diff };
                    });

                register(
                    "armada_get_mission_log",
                    "Get a mission log by line page, or read a definition-of-done output capture by bounded UTF-8 byte pages.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            missionId = new { type = "string", description = "Mission ID (msn_ prefix)" },
                            lines = new { type = "integer", description = "Number of lines to return (default 100)" },
                            offset = new { type = "integer", description = "Line offset to start from (default 0)" },
                            captureId = new { type = "string", description = "Mission-bound DoD capture ID from the definition-of-done report" },
                            offsetBytes = new { type = "integer", description = "Zero-based byte offset for a DoD capture" },
                            lengthBytes = new { type = "integer", description = "DoD capture page size in bytes (maximum 64000)" },
                            sha256 = new { type = "string", description = "Expected DoD capture SHA-256 from the evaluation record" }
                        },
                        required = new[] { "missionId" }
                    },
                    async (args) =>
                    {
                        MissionLogArgs request = JsonSerializer.Deserialize<MissionLogArgs>(args!.Value, _JsonOptions)!;
                        string missionId = request.MissionId;
                        Mission? mission = await McpMissionEvidenceScope.ReadMissionAsync(
                            database, McpCallerContext.Require(), missionId).ConfigureAwait(false);
                        if (mission == null)
                            return (object)await McpMissionEvidenceScope.DenyMissionAsync(database, McpCallerContext.Require(), missionId).ConfigureAwait(false);

                        if (!String.IsNullOrWhiteSpace(request.CaptureId))
                        {
                            return await SessionLogReader.ReadMissionLogCapturePageAsync(
                                settings.LogDirectory,
                                mission.Id,
                                request.CaptureId,
                                request.OffsetBytes,
                                request.LengthBytes,
                                request.Sha256).ConfigureAwait(false);
                        }

                        MissionLogResponse page = await SessionLogReader.ReadMissionLogAsync(
                            settings.LogDirectory, mission.Id, request.Offset, request.Lines, 100, replayId: mission.ResearchReplayId).ConfigureAwait(false);
                        return (object)new { MissionId = page.MissionId, Log = page.Log, Lines = page.Lines, TotalLines = page.TotalLines };
                    });
            }
        }

        private static object ShapeRetryLandingJob(LongRunningJob job, bool joined)
        {
            return new
            {
                job.JobId,
                job.Operation,
                job.SubjectId,
                job.Status,
                job.SubmittedAtUtc,
                job.StartedAtUtc,
                job.VesselId,
                Joined = joined
            };
        }

        private static Mission SanitizeMissionForStatus(Mission mission)
        {
            mission.DiffSnapshot = null;
            mission.AgentOutput = null;
            mission.PlaybookSnapshots = new List<MissionPlaybookSnapshot>();
            if (mission.ReviewComment != null)
                mission.ReviewComment = RuntimeLogFormatter.RedactSecrets(mission.ReviewComment);
            if (mission.FailureReason != null)
                mission.FailureReason = RuntimeLogFormatter.RedactSecrets(mission.FailureReason);
            return mission;
        }
    }
}
