namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Server;

    /// <summary>
    /// Registers MCP tools for objective inspection and creation.
    /// </summary>
    public static class McpObjectiveTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Registers objective MCP tools.
        /// </summary>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            ObjectiveService objectiveService,
            PlanningSessionCoordinator? planningSessionCoordinator = null,
            ObjectiveRefinementCoordinator? objectiveRefinementCoordinator = null,
            ObjectiveDispatchPreviewService? dispatchPreviewService = null)
        {
            register(
                "list_objectives",
                "Enumerate backlog objectives with optional vessel, fleet, status, backlog-state, priority, and free-text filters.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "Optional owner filter" },
                        category = new { type = "string", description = "Optional category filter" },
                        parentObjectiveId = new { type = "string", description = "Optional parent objective filter" },
                        vesselId = new { type = "string", description = "Optional vessel filter" },
                        fleetId = new { type = "string", description = "Optional fleet filter" },
                        status = new { type = "string", description = "Optional lifecycle status filter" },
                        backlogState = new { type = "string", description = "Optional backlog-state filter" },
                        kind = new { type = "string", description = "Optional kind filter" },
                        priority = new { type = "string", description = "Optional priority filter" },
                        effort = new { type = "string", description = "Optional effort filter" },
                        targetVersion = new { type = "string", description = "Optional target-version filter" },
                        search = new { type = "string", description = "Optional free-text search" },
                        pageNumber = new { type = "integer", description = "Optional page number" },
                        pageSize = new { type = "integer", description = "Optional page size" },
                        includeFullContent = new { type = "boolean", description = "Return long free-text fields whole instead of previewing them (default false). Previewed fields carry a companion <name>Length, and the response carries TruncatedFieldCount." }
                    }
                },
                async (args) =>
                {
                    ObjectiveQuery query = JsonSerializer.Deserialize<ObjectiveQuery>(args!.Value, _JsonOptions) ?? new ObjectiveQuery();
                    if (McpResultPreview.WantsDefaultPageSize(args)) query.PageSize = McpResultPreview.DefaultMcpPageSize;
                    AuthContext auth = McpCallerContext.Require();
                    object result = await objectiveService.EnumerateAsync(auth, query).ConfigureAwait(false);
                    bool wantsFull = args.HasValue
                        && args.Value.TryGetProperty("includeFullContent", out JsonElement _full)
                        && _full.ValueKind == JsonValueKind.True;
                    return McpResultPreview.Apply(result, wantsFull);
                });

            register(
                "list_backlog",
                "Enumerate backlog items with optional vessel, fleet, status, backlog-state, priority, and free-text filters.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        owner = new { type = "string", description = "Optional owner filter" },
                        category = new { type = "string", description = "Optional category filter" },
                        parentObjectiveId = new { type = "string", description = "Optional parent backlog item filter" },
                        vesselId = new { type = "string", description = "Optional vessel filter" },
                        fleetId = new { type = "string", description = "Optional fleet filter" },
                        status = new { type = "string", description = "Optional lifecycle status filter" },
                        backlogState = new { type = "string", description = "Optional backlog-state filter" },
                        kind = new { type = "string", description = "Optional kind filter" },
                        priority = new { type = "string", description = "Optional priority filter" },
                        effort = new { type = "string", description = "Optional effort filter" },
                        targetVersion = new { type = "string", description = "Optional target-version filter" },
                        search = new { type = "string", description = "Optional free-text search" },
                        pageNumber = new { type = "integer", description = "Optional page number" },
                        pageSize = new { type = "integer", description = "Optional page size" },
                        includeFullContent = new { type = "boolean", description = "Return long free-text fields whole instead of previewing them (default false). Previewed fields carry a companion <name>Length, and the response carries TruncatedFieldCount." }
                    }
                },
                async (args) =>
                {
                    ObjectiveQuery query = JsonSerializer.Deserialize<ObjectiveQuery>(args!.Value, _JsonOptions) ?? new ObjectiveQuery();
                    if (McpResultPreview.WantsDefaultPageSize(args)) query.PageSize = McpResultPreview.DefaultMcpPageSize;
                    AuthContext auth = McpCallerContext.Require();
                    object result = await objectiveService.EnumerateAsync(auth, query).ConfigureAwait(false);
                    bool wantsFull = args.HasValue
                        && args.Value.TryGetProperty("includeFullContent", out JsonElement _full)
                        && _full.ValueKind == JsonValueKind.True;
                    return McpResultPreview.Apply(result, wantsFull);
                });

            register(
                "get_objective",
                "Inspect one objective including linked repositories, planning sessions, voyages, releases, deployments, incidents, and acceptance criteria.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        objectiveId = new { type = "string", description = "Objective ID (obj_ prefix)" }
                    },
                    required = new[] { "objectiveId" }
                },
                async (args) =>
                {
                    ObjectiveIdArgs request = JsonSerializer.Deserialize<ObjectiveIdArgs>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize ObjectiveIdArgs.");
                    AuthContext auth = McpCallerContext.Require();
                    Objective? objective = await objectiveService.ReadAsync(auth, request.ObjectiveId).ConfigureAwait(false);
                    if (objective == null) return (object)new { Error = "Objective not found" };
                    return (object)objective;
                });

            register(
                "get_backlog_item",
                "Inspect one backlog item including linked repositories, planning sessions, voyages, releases, deployments, incidents, and acceptance criteria.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        objectiveId = new { type = "string", description = "Backlog item ID (obj_ prefix)" }
                    },
                    required = new[] { "objectiveId" }
                },
                async (args) =>
                {
                    ObjectiveIdArgs request = JsonSerializer.Deserialize<ObjectiveIdArgs>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize ObjectiveIdArgs.");
                    AuthContext auth = McpCallerContext.Require();
                    Objective? objective = await objectiveService.ReadAsync(auth, request.ObjectiveId).ConfigureAwait(false);
                    if (objective == null) return (object)new { Error = "Backlog item not found" };
                    return (object)objective;
                });

            if (dispatchPreviewService != null)
            {
                register(
                    "preview_objective_dispatch",
                    "Build a read-only dispatch preview for one persisted objective. Reports all target, pipeline, captain, verification, provisioning, dependency, and brief blockers without creating fleet state. Busy captains are capacity information and do not make a configured role unready.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            objectiveId = new { type = "string", description = "Objective or backlog item ID (obj_ prefix)" },
                            vesselId = new { type = "string", description = "Optional target vessel override" },
                            pipelineId = new { type = "string", description = "Optional pipeline ID or name override" },
                            captainAssignments = new
                            {
                                type = "array",
                                description = "Optional per-persona captain routing overrides to evaluate",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        persona = new { type = "string", description = "Pipeline persona" },
                                        captainId = new { type = "string", description = "Optional preferred captain ID" },
                                        fallbackTier = new { type = "string", description = "Optional fallback tier: Economy, Standard, or Premium" }
                                    },
                                    required = new[] { "persona" }
                                }
                            }
                        },
                        required = new[] { "objectiveId" }
                    },
                    async (args) =>
                    {
                        ObjectiveDispatchPreviewArgs request = JsonSerializer.Deserialize<ObjectiveDispatchPreviewArgs>(args!.Value, _JsonOptions)
                            ?? throw new InvalidOperationException("Could not deserialize ObjectiveDispatchPreviewArgs.");
                        AuthContext auth = McpCallerContext.Require();
                        Objective? objective = await objectiveService.ReadAsync(auth, request.ObjectiveId).ConfigureAwait(false);
                        if (objective == null)
                        {
                            return (object)new
                            {
                                Error = "Objective not found: " + request.ObjectiveId,
                                Code = "objective_not_found",
                                ObjectiveId = request.ObjectiveId
                            };
                        }

                        return (object)await dispatchPreviewService.PreviewAsync(
                            auth,
                            objective,
                            request.VesselId,
                            request.PipelineId,
                            request.CaptainAssignments).ConfigureAwait(false);
                    });
            }

            register(
                "create_objective",
                "Create an internal-first objective or intake-style record that can link vessels, planning sessions, voyages, checks, releases, deployments, and incidents.",
                ObjectiveWriteSchema("objective", false),
                async (args) => await CreateAsync(objectiveService, args, "objective_create_failed").ConfigureAwait(false));

            register(
                "create_backlog_item",
                "Create a backlog item that can link vessels, planning sessions, voyages, checks, releases, deployments, and incidents.",
                ObjectiveWriteSchema("backlog item", false),
                async (args) => await CreateAsync(objectiveService, args, "backlog_create_failed").ConfigureAwait(false));

            register(
                "update_objective",
                "Update one objective/backlog entry, including prioritization, category, linked entities, and refinement metadata. Requires one of objectiveId, backlogItemId, or id. Only supplied fields change; an empty string clears a clearable text field.",
                ObjectiveWriteSchema("objective", true),
                async (args) => await UpdateAsync(objectiveService, args, "objective_update_failed", "objectiveId is required", null).ConfigureAwait(false));

            register(
                "update_backlog_item",
                "Update one backlog item, including prioritization, category, linked entities, and refinement metadata. Requires one of objectiveId, backlogItemId, or id. Only supplied fields change; an empty string clears a clearable text field.",
                ObjectiveWriteSchema("backlog item", true),
                async (args) => await UpdateAsync(objectiveService, args, "backlog_update_failed", "objectiveId is required", "backlog_item_id_required").ConfigureAwait(false));

            register(
                "reorder_objectives",
                "Apply one or more explicit backlog rank updates using objective-compatible tool naming.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        items = new
                        {
                            type = "array",
                            description = "Ordered list of explicit objective rank updates",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    objectiveId = new { type = "string", description = "Objective ID (obj_ prefix)" },
                                    rank = new { type = "integer", description = "New deterministic rank" }
                                },
                                required = new[] { "objectiveId", "rank" }
                            }
                        }
                    },
                    required = new[] { "items" }
                },
                async (args) =>
                {
                    ObjectiveReorderRequest request = JsonSerializer.Deserialize<ObjectiveReorderRequest>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize ObjectiveReorderRequest.");
                    AuthContext auth = McpCallerContext.Require();
                    return (object)await objectiveService.ReorderAsync(auth, request).ConfigureAwait(false);
                });

            register(
                "reorder_backlog_items",
                "Apply one or more explicit backlog rank updates using backlog terminology.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        items = new
                        {
                            type = "array",
                            description = "Ordered list of explicit backlog item rank updates",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    objectiveId = new { type = "string", description = "Backlog item ID (obj_ prefix)" },
                                    rank = new { type = "integer", description = "New deterministic rank" }
                                },
                                required = new[] { "objectiveId", "rank" }
                            }
                        }
                    },
                    required = new[] { "items" }
                },
                async (args) =>
                {
                    ObjectiveReorderRequest request = JsonSerializer.Deserialize<ObjectiveReorderRequest>(args!.Value, _JsonOptions)
                        ?? throw new InvalidOperationException("Could not deserialize ObjectiveReorderRequest.");
                    AuthContext auth = McpCallerContext.Require();
                    return (object)await objectiveService.ReorderAsync(auth, request).ConfigureAwait(false);
                });

            if (objectiveRefinementCoordinator != null)
            {
                register(
                    "list_backlog_refinement_sessions",
                    "List captain-backed refinement sessions for one backlog item.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            objectiveId = new { type = "string", description = "Backlog item ID (obj_ prefix)" }
                        },
                        required = new[] { "objectiveId" }
                    },
                    async (args) =>
                    {
                        ObjectiveIdArgs request = JsonSerializer.Deserialize<ObjectiveIdArgs>(args!.Value, _JsonOptions)
                            ?? throw new InvalidOperationException("Could not deserialize ObjectiveIdArgs.");
                        AuthContext auth = McpCallerContext.Require();
                        Objective? objective = await objectiveService.ReadAsync(auth, request.ObjectiveId).ConfigureAwait(false);
                        if (objective == null) return (object)new { Error = "Backlog item not found" };

                        List<ObjectiveRefinementSession> sessions = await EnumerateObjectiveRefinementSessionsAsync(database, auth, objective.Id).ConfigureAwait(false);
                        return (object)sessions
                            .OrderByDescending(session => session.LastUpdateUtc)
                            .ToList();
                    });

                register(
                    "create_backlog_refinement_session",
                    "Start a captain-backed refinement session for a backlog item. The caller must specify the captain explicitly.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            objectiveId = new { type = "string", description = "Backlog item ID (obj_ prefix)" },
                            captainId = new { type = "string", description = "Captain ID (cpt_ prefix)" },
                            fleetId = new { type = "string", description = "Optional fleet ID override" },
                            vesselId = new { type = "string", description = "Optional vessel ID override" },
                            title = new { type = "string", description = "Optional refinement session title" },
                            initialMessage = new { type = "string", description = "Optional initial prompt or refinement request" }
                        },
                        required = new[] { "objectiveId", "captainId" }
                    },
                    async (args) =>
                    {
                        CreateBacklogRefinementSessionArgs request = JsonSerializer.Deserialize<CreateBacklogRefinementSessionArgs>(args!.Value, _JsonOptions)
                            ?? throw new InvalidOperationException("Could not deserialize CreateBacklogRefinementSessionArgs.");
                        AuthContext auth = McpCallerContext.Require();
                        Objective objective = await objectiveService.ReadAsync(auth, request.ObjectiveId).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Backlog item not found.");
                        Captain captain = await ReadCaptainForContextAsync(database, auth, request.CaptainId).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Captain not found.");
                        string? vesselId = !String.IsNullOrWhiteSpace(request.VesselId) ? request.VesselId : objective.VesselIds.FirstOrDefault();
                        Vessel? vessel = !String.IsNullOrWhiteSpace(vesselId)
                            ? await ReadVesselForContextAsync(database, auth, vesselId!).ConfigureAwait(false)
                            : null;
                        if (!String.IsNullOrWhiteSpace(vesselId) && vessel == null)
                            throw new InvalidOperationException("Vessel not found.");

                        ObjectiveRefinementSession session = await objectiveRefinementCoordinator
                            .CreateAsync(auth.TenantId, auth.UserId, objective, captain, vessel, request.ToCreateRequest())
                            .ConfigureAwait(false);
                        await objectiveService.LinkRefinementSessionAsync(auth, objective.Id, session.Id).ConfigureAwait(false);
                        return (object)await BuildObjectiveRefinementSessionDetailAsync(database, objectiveService, auth, session).ConfigureAwait(false);
                    });

                register(
                    "get_backlog_refinement_session",
                    "Inspect one backlog refinement session, including its transcript, captain, vessel, and linked backlog item.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            sessionId = new { type = "string", description = "Objective refinement session ID (ors_ prefix)" }
                        },
                        required = new[] { "sessionId" }
                    },
                    async (args) =>
                    {
                        ObjectiveRefinementSessionIdArgs request = JsonSerializer.Deserialize<ObjectiveRefinementSessionIdArgs>(args!.Value, _JsonOptions)
                            ?? throw new InvalidOperationException("Could not deserialize ObjectiveRefinementSessionIdArgs.");
                        AuthContext auth = McpCallerContext.Require();
                        ObjectiveRefinementSession session = await ReadObjectiveRefinementSessionForContextAsync(database, auth, request.SessionId).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Objective refinement session not found.");
                        return (object)await BuildObjectiveRefinementSessionDetailAsync(database, objectiveService, auth, session).ConfigureAwait(false);
                    });

                register(
                    "send_backlog_refinement_message",
                    "Append one user message to a backlog refinement session and launch the next captain turn.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            sessionId = new { type = "string", description = "Objective refinement session ID (ors_ prefix)" },
                            content = new { type = "string", description = "Message content" }
                        },
                        required = new[] { "sessionId", "content" }
                    },
                    async (args) =>
                    {
                        SendBacklogRefinementMessageArgs request = JsonSerializer.Deserialize<SendBacklogRefinementMessageArgs>(args!.Value, _JsonOptions)
                            ?? throw new InvalidOperationException("Could not deserialize SendBacklogRefinementMessageArgs.");
                        AuthContext auth = McpCallerContext.Require();
                        ObjectiveRefinementSession session = await ReadObjectiveRefinementSessionForContextAsync(database, auth, request.SessionId).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Objective refinement session not found.");
                        await objectiveRefinementCoordinator.SendMessageAsync(session, request.Content).ConfigureAwait(false);
                        return (object)await BuildObjectiveRefinementSessionDetailAsync(database, objectiveService, auth, session).ConfigureAwait(false);
                    });

                register(
                    "summarize_backlog_refinement_session",
                    "Create or select a structured backlog-refinement summary from one refinement session.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            sessionId = new { type = "string", description = "Objective refinement session ID (ors_ prefix)" },
                            messageId = new { type = "string", description = "Optional transcript message ID to summarize instead of the latest assistant turn" }
                        },
                        required = new[] { "sessionId" }
                    },
                    async (args) =>
                    {
                        SummarizeBacklogRefinementArgs request = JsonSerializer.Deserialize<SummarizeBacklogRefinementArgs>(args!.Value, _JsonOptions)
                            ?? throw new InvalidOperationException("Could not deserialize SummarizeBacklogRefinementArgs.");
                        AuthContext auth = McpCallerContext.Require();
                        ObjectiveRefinementSession session = await ReadObjectiveRefinementSessionForContextAsync(database, auth, request.SessionId).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Objective refinement session not found.");
                        return (object)await objectiveRefinementCoordinator.SummarizeAsync(session, request.ToSummaryRequest()).ConfigureAwait(false);
                    });

                register(
                    "apply_backlog_refinement_summary",
                    "Apply a refinement summary back to the linked backlog item and optionally promote its backlog state.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            sessionId = new { type = "string", description = "Objective refinement session ID (ors_ prefix)" },
                            messageId = new { type = "string", description = "Optional transcript message ID to summarize and apply" },
                            markMessageSelected = new { type = "boolean", description = "Whether to mark the summarized message as selected (default true)" },
                            promoteBacklogState = new { type = "boolean", description = "Whether to promote the backlog state based on refinement output (default true)" }
                        },
                        required = new[] { "sessionId" }
                    },
                    async (args) =>
                    {
                        ApplyBacklogRefinementArgs request = JsonSerializer.Deserialize<ApplyBacklogRefinementArgs>(args!.Value, _JsonOptions)
                            ?? throw new InvalidOperationException("Could not deserialize ApplyBacklogRefinementArgs.");
                        AuthContext auth = McpCallerContext.Require();
                        ObjectiveRefinementSession session = await ReadObjectiveRefinementSessionForContextAsync(database, auth, request.SessionId).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Objective refinement session not found.");
                        Objective objective = await objectiveService.ReadAsync(auth, session.ObjectiveId).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Backlog item not found.");
                        (ObjectiveRefinementSummaryResponse summary, Objective updated) = await objectiveRefinementCoordinator
                            .ApplyAsync(auth, objective, session, request.ToApplyRequest(), objectiveService)
                            .ConfigureAwait(false);
                        return (object)new ObjectiveRefinementApplyResponse
                        {
                            Summary = summary,
                            Objective = updated
                        };
                    });

                register(
                    "stop_backlog_refinement_session",
                    "Request stop for one active backlog refinement session and release the selected captain.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            sessionId = new { type = "string", description = "Objective refinement session ID (ors_ prefix)" }
                        },
                        required = new[] { "sessionId" }
                    },
                    async (args) =>
                    {
                        ObjectiveRefinementSessionIdArgs request = JsonSerializer.Deserialize<ObjectiveRefinementSessionIdArgs>(args!.Value, _JsonOptions)
                            ?? throw new InvalidOperationException("Could not deserialize ObjectiveRefinementSessionIdArgs.");
                        AuthContext auth = McpCallerContext.Require();
                        ObjectiveRefinementSession session = await ReadObjectiveRefinementSessionForContextAsync(database, auth, request.SessionId).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Objective refinement session not found.");
                        ObjectiveRefinementSession stopping = await objectiveRefinementCoordinator.RequestStopAsync(session).ConfigureAwait(false);
                        return (object)await BuildObjectiveRefinementSessionDetailAsync(database, objectiveService, auth, stopping).ConfigureAwait(false);
                    });

                register(
                    "delete_backlog_refinement_session",
                    "Delete one backlog refinement session and its transcript, and remove it from the backlog item's links. An active session is stopped first.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            sessionId = new { type = "string", description = "Objective refinement session ID (ors_ prefix)" }
                        },
                        required = new[] { "sessionId" }
                    },
                    async (args) =>
                    {
                        ObjectiveRefinementSessionIdArgs request = JsonSerializer.Deserialize<ObjectiveRefinementSessionIdArgs>(args!.Value, _JsonOptions)
                            ?? new ObjectiveRefinementSessionIdArgs();
                        AuthContext auth = McpCallerContext.Require();
                        ObjectiveRefinementSession? session = String.IsNullOrWhiteSpace(request.SessionId)
                            ? null
                            : await ReadObjectiveRefinementSessionForContextAsync(database, auth, request.SessionId).ConfigureAwait(false);
                        if (session == null) return (object)new { Error = "Objective refinement session not found", Code = "not_found" };
                        try
                        {
                            await objectiveRefinementCoordinator.DeleteAndUnlinkAsync(auth, session, objectiveService).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException ex)
                        {
                            return (object)new { Error = ex.Message, Code = "conflict" };
                        }

                        return (object)new { Status = "deleted", SessionId = session.Id, ObjectiveId = session.ObjectiveId };
                    });
            }

            if (planningSessionCoordinator != null)
            {
                register(
                    "create_backlog_planning_session",
                    "Create a repository-aware planning session from a backlog item while preserving the objective linkage.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            objectiveId = new { type = "string", description = "Backlog item ID (obj_ prefix)" },
                            captainId = new { type = "string", description = "Captain ID (cpt_ prefix)" },
                            vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                            fleetId = new { type = "string", description = "Optional fleet ID override" },
                            pipelineId = new { type = "string", description = "Optional pipeline ID override" },
                            title = new { type = "string", description = "Optional planning session title" },
                            selectedPlaybooks = new { type = "array", items = new { type = "object" }, description = "Optional ordered playbook selections" }
                        },
                        required = new[] { "objectiveId", "captainId", "vesselId" }
                    },
                    async (args) =>
                    {
                        CreateBacklogPlanningSessionArgs request = JsonSerializer.Deserialize<CreateBacklogPlanningSessionArgs>(args!.Value, _JsonOptions)
                            ?? throw new InvalidOperationException("Could not deserialize CreateBacklogPlanningSessionArgs.");
                        AuthContext auth = McpCallerContext.Require();
                        string objectiveId = String.IsNullOrWhiteSpace(request.ObjectiveId)
                            ? throw new InvalidOperationException("Objective ID is required.")
                            : request.ObjectiveId;
                        Objective objective = await objectiveService.ReadAsync(auth, objectiveId).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Backlog item not found.");
                        Captain captain = await ReadCaptainForContextAsync(database, auth, request.CaptainId).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Captain not found.");
                        Vessel vessel = await ReadVesselForContextAsync(database, auth, request.VesselId).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Vessel not found.");

                        PlanningSession session = await planningSessionCoordinator
                            .CreateAsync(auth.TenantId, auth.UserId, captain, vessel, request.ToPlanningSessionCreateRequest())
                            .ConfigureAwait(false);
                        await objectiveService.LinkPlanningSessionAsync(auth, objective.Id, session.Id).ConfigureAwait(false);
                        return (object)await BuildPlanningSessionDetailAsync(database, objectiveService, auth, session).ConfigureAwait(false);
                    });

                register(
                    "get_backlog_planning_session",
                    "Inspect one planning session created from backlog/objective work, including its transcript and linked backlog items.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            sessionId = new { type = "string", description = "Planning session ID (psn_ prefix)" }
                        },
                        required = new[] { "sessionId" }
                    },
                    async (args) =>
                    {
                        PlanningSessionIdArgs request = JsonSerializer.Deserialize<PlanningSessionIdArgs>(args!.Value, _JsonOptions)
                            ?? throw new InvalidOperationException("Could not deserialize PlanningSessionIdArgs.");
                        AuthContext auth = McpCallerContext.Require();
                        PlanningSession session = await ReadPlanningSessionForContextAsync(database, auth, request.SessionId).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Planning session not found.");
                        return (object)await BuildPlanningSessionDetailAsync(database, objectiveService, auth, session).ConfigureAwait(false);
                    });

                register(
                    "dispatch_backlog_planning_session",
                    "Dispatch a voyage from a planning session and keep the linked backlog item associated with the resulting voyage.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            sessionId = new { type = "string", description = "Planning session ID (psn_ prefix)" },
                            messageId = new { type = "string", description = "Optional transcript message ID to dispatch from" },
                            title = new { type = "string", description = "Optional voyage title override" },
                            description = new { type = "string", description = "Optional mission description override" }
                        },
                        required = new[] { "sessionId" }
                    },
                    async (args) =>
                    {
                        DispatchBacklogPlanningSessionArgs request = JsonSerializer.Deserialize<DispatchBacklogPlanningSessionArgs>(args!.Value, _JsonOptions)
                            ?? throw new InvalidOperationException("Could not deserialize DispatchBacklogPlanningSessionArgs.");
                        AuthContext auth = McpCallerContext.Require();
                        PlanningSession session = await ReadPlanningSessionForContextAsync(database, auth, request.SessionId).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("Planning session not found.");
                        Voyage voyage = await planningSessionCoordinator.DispatchAsync(session, request.ToDispatchRequest()).ConfigureAwait(false);
                        // The planning dispatch links every session objective inside admission; read the result back.
                        List<Objective> updatedObjectives = (await objectiveService.EnumerateAsync(auth, new ObjectiveQuery
                        {
                            PageNumber = 1,
                            PageSize = 500,
                            VoyageId = voyage.Id
                        }).ConfigureAwait(false)).Objects;

                        return (object)new
                        {
                            Voyage = voyage,
                            Objectives = updatedObjectives,
                            ObjectiveIds = updatedObjectives.Select(item => item.Id).ToList()
                        };
                    });
            }

            register(
                "delete_objective",
                "Delete one objective/backlog entry and its snapshot history.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        objectiveId = new { type = "string", description = "Objective ID (obj_ prefix)" }
                    },
                    required = new[] { "objectiveId" }
                },
                async (args) =>
                {
                    ObjectiveIdArgs request = JsonSerializer.Deserialize<ObjectiveIdArgs>(args!.Value, _JsonOptions) ?? new ObjectiveIdArgs();
                    RecordWriteResult<Objective> result = await objectiveService.DeleteRecordAsync(McpCallerContext.Require(), request.ObjectiveId).ConfigureAwait(false);
                    if (!result.Succeeded) return WriteError(result, "objective_delete_failed", request.ObjectiveId, false);
                    return (object)new { Success = true, ObjectiveId = result.Record!.Id };
                });

            register(
                "delete_backlog_item",
                "Delete one backlog item and its snapshot history.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        objectiveId = new { type = "string", description = "Backlog item ID (obj_ prefix)" }
                    },
                    required = new[] { "objectiveId" }
                },
                async (args) =>
                {
                    ObjectiveIdArgs request = JsonSerializer.Deserialize<ObjectiveIdArgs>(args!.Value, _JsonOptions) ?? new ObjectiveIdArgs();
                    RecordWriteResult<Objective> result = await objectiveService.DeleteRecordAsync(McpCallerContext.Require(), request.ObjectiveId).ConfigureAwait(false);
                    if (!result.Succeeded) return WriteError(result, "backlog_delete_failed", request.ObjectiveId, false);
                    return (object)new { Success = true, ObjectiveId = result.Record!.Id };
                });
        }

        private static object BuildValidEnumMap()
        {
            return new
            {
                status = Enum.GetNames<ObjectiveStatusEnum>(),
                kind = Enum.GetNames<ObjectiveKindEnum>(),
                priority = Enum.GetNames<ObjectivePriorityEnum>(),
                backlogState = Enum.GetNames<ObjectiveBacklogStateEnum>(),
                effort = Enum.GetNames<ObjectiveEffortEnum>()
            };
        }

        private static async Task<List<ObjectiveRefinementSession>> EnumerateObjectiveRefinementSessionsAsync(
            DatabaseDriver database,
            AuthContext auth,
            string objectiveId)
        {
            List<ObjectiveRefinementSession> sessions = auth.IsAdmin
                ? await database.ObjectiveRefinementSessions.EnumerateAsync().ConfigureAwait(false)
                : auth.IsTenantAdmin
                    ? await database.ObjectiveRefinementSessions.EnumerateAsync(auth.TenantId!).ConfigureAwait(false)
                    : await database.ObjectiveRefinementSessions.EnumerateAsync(auth.TenantId!, auth.UserId!).ConfigureAwait(false);
            return sessions
                .Where(session => String.Equals(session.ObjectiveId, objectiveId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static async Task<PlanningSession?> ReadPlanningSessionForContextAsync(
            DatabaseDriver database,
            AuthContext auth,
            string id)
        {
            if (auth.IsAdmin)
                return await database.PlanningSessions.ReadAsync(id).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await database.PlanningSessions.ReadAsync(auth.TenantId!, id).ConfigureAwait(false);
            return await database.PlanningSessions.ReadAsync(auth.TenantId!, auth.UserId!, id).ConfigureAwait(false);
        }

        private static async Task<ObjectiveRefinementSession?> ReadObjectiveRefinementSessionForContextAsync(
            DatabaseDriver database,
            AuthContext auth,
            string id)
        {
            if (auth.IsAdmin)
                return await database.ObjectiveRefinementSessions.ReadAsync(id).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await database.ObjectiveRefinementSessions.ReadAsync(auth.TenantId!, id).ConfigureAwait(false);
            return await database.ObjectiveRefinementSessions.ReadAsync(auth.TenantId!, auth.UserId!, id).ConfigureAwait(false);
        }

        private static async Task<Captain?> ReadCaptainForContextAsync(
            DatabaseDriver database,
            AuthContext auth,
            string id)
        {
            if (auth.IsAdmin)
                return await database.Captains.ReadAsync(id).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await database.Captains.ReadAsync(auth.TenantId!, id).ConfigureAwait(false);
            return await database.Captains.ReadAsync(auth.TenantId!, auth.UserId!, id).ConfigureAwait(false);
        }

        private static async Task<Vessel?> ReadVesselForContextAsync(
            DatabaseDriver database,
            AuthContext auth,
            string id)
        {
            if (auth.IsAdmin)
                return await database.Vessels.ReadAsync(id).ConfigureAwait(false);
            if (auth.IsTenantAdmin)
                return await database.Vessels.ReadAsync(auth.TenantId!, id).ConfigureAwait(false);
            return await database.Vessels.ReadAsync(auth.TenantId!, auth.UserId!, id).ConfigureAwait(false);
        }

        private static async Task<object> BuildPlanningSessionDetailAsync(
            DatabaseDriver database,
            ObjectiveService objectiveService,
            AuthContext auth,
            PlanningSession session)
        {
            PlanningSession refreshed = await ReadPlanningSessionForContextAsync(database, auth, session.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Planning session not found: " + session.Id);
            List<PlanningSessionMessage> messages = await database.PlanningSessionMessages
                .EnumerateBySessionAsync(refreshed.Id)
                .ConfigureAwait(false);
            Captain? captain = await ReadCaptainForContextAsync(database, auth, refreshed.CaptainId).ConfigureAwait(false);
            Vessel? vessel = await ReadVesselForContextAsync(database, auth, refreshed.VesselId).ConfigureAwait(false);
            List<Objective> linkedObjectives = await objectiveService.EnumerateByPlanningSessionAsync(auth, refreshed.Id).ConfigureAwait(false);

            return new
            {
                Session = refreshed,
                Messages = messages.OrderBy(message => message.Sequence).ToList(),
                Captain = captain,
                Vessel = vessel,
                Objectives = linkedObjectives,
                ObjectiveIds = linkedObjectives.Select(objective => objective.Id).ToList()
            };
        }

        private static async Task<ObjectiveRefinementSessionDetail> BuildObjectiveRefinementSessionDetailAsync(
            DatabaseDriver database,
            ObjectiveService objectiveService,
            AuthContext auth,
            ObjectiveRefinementSession session)
        {
            ObjectiveRefinementSession refreshed = await ReadObjectiveRefinementSessionForContextAsync(database, auth, session.Id).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Objective refinement session not found: " + session.Id);
            List<ObjectiveRefinementMessage> messages = await database.ObjectiveRefinementMessages
                .EnumerateBySessionAsync(refreshed.Id)
                .ConfigureAwait(false);
            Captain? captain = await ReadCaptainForContextAsync(database, auth, refreshed.CaptainId).ConfigureAwait(false);
            Vessel? vessel = !String.IsNullOrWhiteSpace(refreshed.VesselId)
                ? await ReadVesselForContextAsync(database, auth, refreshed.VesselId!).ConfigureAwait(false)
                : null;
            Objective? objective = await objectiveService.ReadAsync(auth, refreshed.ObjectiveId).ConfigureAwait(false);

            return new ObjectiveRefinementSessionDetail
            {
                Session = refreshed,
                Messages = messages.OrderBy(message => message.Sequence).ToList(),
                Captain = captain,
                Vessel = vessel,
                Objective = objective
            };
        }

        private sealed class PlanningSessionIdArgs
        {
            public string SessionId { get; set; } = String.Empty;
        }

        private sealed class ObjectiveRefinementSessionIdArgs
        {
            public string SessionId { get; set; } = String.Empty;
        }

        private sealed class CreateBacklogPlanningSessionArgs : PlanningSessionCreateRequest
        {
            public PlanningSessionCreateRequest ToPlanningSessionCreateRequest()
            {
                return new PlanningSessionCreateRequest
                {
                    Title = Title,
                    CaptainId = CaptainId,
                    VesselId = VesselId,
                    FleetId = FleetId,
                    PipelineId = PipelineId,
                    SelectedPlaybooks = SelectedPlaybooks ?? new List<SelectedPlaybook>(),
                    ObjectiveId = ObjectiveId
                };
            }
        }

        private sealed class DispatchBacklogPlanningSessionArgs : PlanningSessionDispatchRequest
        {
            public string SessionId { get; set; } = String.Empty;

            public PlanningSessionDispatchRequest ToDispatchRequest()
            {
                return new PlanningSessionDispatchRequest
                {
                    MessageId = MessageId,
                    Title = Title,
                    Description = Description
                };
            }
        }

        private sealed class CreateBacklogRefinementSessionArgs : ObjectiveRefinementSessionCreateRequest
        {
            public string ObjectiveId { get; set; } = String.Empty;

            public ObjectiveRefinementSessionCreateRequest ToCreateRequest()
            {
                return new ObjectiveRefinementSessionCreateRequest
                {
                    CaptainId = CaptainId,
                    FleetId = FleetId,
                    VesselId = VesselId,
                    Title = Title,
                    InitialMessage = InitialMessage
                };
            }
        }

        private sealed class SendBacklogRefinementMessageArgs : ObjectiveRefinementMessageRequest
        {
            public string SessionId { get; set; } = String.Empty;
        }

        private sealed class SummarizeBacklogRefinementArgs : ObjectiveRefinementSummaryRequest
        {
            public string SessionId { get; set; } = String.Empty;

            public ObjectiveRefinementSummaryRequest ToSummaryRequest()
            {
                return new ObjectiveRefinementSummaryRequest
                {
                    MessageId = MessageId
                };
            }
        }

        private sealed class ApplyBacklogRefinementArgs : ObjectiveRefinementApplyRequest
        {
            public string SessionId { get; set; } = String.Empty;

            public ObjectiveRefinementApplyRequest ToApplyRequest()
            {
                return new ObjectiveRefinementApplyRequest
                {
                    MessageId = MessageId,
                    MarkMessageSelected = MarkMessageSelected,
                    PromoteBacklogState = PromoteBacklogState
                };
            }
        }

        private sealed class UpdateObjectiveArgs : ObjectiveUpsertRequest
        {
            public string ObjectiveId { get; set; } = String.Empty;

            public string BacklogItemId { get; set; } = String.Empty;

            public string Id { get; set; } = String.Empty;

            public string ResolveObjectiveId()
            {
                if (!String.IsNullOrWhiteSpace(ObjectiveId)) return ObjectiveId.Trim();
                if (!String.IsNullOrWhiteSpace(BacklogItemId)) return BacklogItemId.Trim();
                if (!String.IsNullOrWhiteSpace(Id)) return Id.Trim();
                return String.Empty;
            }
        }

        /// <summary>
        /// The one input schema for objective and backlog-item create and update. A clearable text field
        /// declares emptyStringClears, so an explicit empty string reaches the service and clears the stored
        /// value as it does on REST instead of being dropped as omitted.
        /// </summary>
        private static Dictionary<string, object> ObjectiveWriteSchema(string noun, bool update)
        {
            Dictionary<string, object> properties = new Dictionary<string, object>();
            if (update)
            {
                properties["objectiveId"] = new { type = "string", description = "ID of the " + noun + " (obj_ prefix)" };
                properties["backlogItemId"] = new { type = "string", description = "Alias for objectiveId" };
                properties["id"] = new { type = "string", description = "Alias for objectiveId" };
            }

            properties["title"] = new { type = "string", description = update ? "New title" : "Title of the " + noun };
            properties["description"] = ClearableText("Long-form description");
            properties["status"] = new { type = "string", description = "Lifecycle status such as Draft, Scoped, Planned, or InProgress" };
            properties["kind"] = new { type = "string", description = "Backlog kind such as Feature, Bug, Refactor, or Research" };
            properties["category"] = ClearableText("Category such as Frontend, Backend, DevEx, or Ops");
            properties["priority"] = new { type = "string", description = "Priority such as P0, P1, P2, or P3" };
            properties["rank"] = new { type = "integer", description = "Deterministic backlog rank" };
            properties["autoDispatchEnabled"] = new { type = "boolean", description = "Whether the autonomous scheduler may dispatch this " + noun + " (default false on create)" };
            properties["backlogState"] = new { type = "string", description = "Backlog state such as Inbox or ReadyForDispatch" };
            properties["effort"] = new { type = "string", description = "Effort such as XS, S, M, L, or XL" };
            properties["owner"] = ClearableText("Owner display label");
            properties["targetVersion"] = ClearableText("Target release version");
            properties["dueUtc"] = new { type = "string", description = "Due timestamp in UTC" };
            properties["parentObjectiveId"] = ClearableText("Parent " + noun + " identifier");
            properties["blockedByObjectiveIds"] = new { type = "array", items = new { type = "string" }, description = "Blocking " + noun + " identifiers" };
            properties["refinementSummary"] = ClearableText("Captain-generated refinement summary");
            properties["preparation"] = BuildPreparationSchema();
            properties["suggestedPipelineId"] = ClearableText("Suggested pipeline identifier");
            properties["suggestedPlaybooks"] = BuildPlaybookSelectionSchema();
            properties["startFromRef"] = ClearableText("Commit or ref in the vessel repository that the next dispatched voyage starts from; dispatch refuses an unresolvable ref");
            properties["refinementSessionIds"] = new { type = "array", items = new { type = "string" }, description = "Linked refinement-session IDs" };
            properties["tags"] = new { type = "array", items = new { type = "string" }, description = "Tags" };
            properties["acceptanceCriteria"] = new { type = "array", items = new { type = "string" }, description = "Acceptance criteria" };
            properties["nonGoals"] = new { type = "array", items = new { type = "string" }, description = "Explicit non-goals" };
            properties["rolloutConstraints"] = new { type = "array", items = new { type = "string" }, description = "Rollout constraints" };
            properties["evidenceLinks"] = new { type = "array", items = new { type = "string" }, description = "Evidence or source links" };
            properties["fleetIds"] = new { type = "array", items = new { type = "string" }, description = "Linked fleet IDs" };
            properties["vesselIds"] = new { type = "array", items = new { type = "string" }, description = "Linked vessel IDs" };
            properties["planningSessionIds"] = new { type = "array", items = new { type = "string" }, description = "Linked planning-session IDs" };
            properties["voyageIds"] = new { type = "array", items = new { type = "string" }, description = "Linked voyage IDs" };
            properties["missionIds"] = new { type = "array", items = new { type = "string" }, description = "Linked mission IDs" };
            properties["checkRunIds"] = new { type = "array", items = new { type = "string" }, description = "Linked check-run IDs" };
            properties["releaseIds"] = new { type = "array", items = new { type = "string" }, description = "Linked release IDs" };
            properties["deploymentIds"] = new { type = "array", items = new { type = "string" }, description = "Linked deployment IDs" };
            properties["incidentIds"] = new { type = "array", items = new { type = "string" }, description = "Linked incident IDs" };

            Dictionary<string, object> schema = new Dictionary<string, object>();
            schema["type"] = "object";
            schema["properties"] = properties;
            if (!update) schema["required"] = new[] { "title" };
            return schema;
        }

        private static object ClearableText(string description)
        {
            return new { type = "string", description = description + ". An empty string clears it.", emptyStringClears = true };
        }

        private static async Task<object> CreateAsync(ObjectiveService objectiveService, JsonElement? args, string code)
        {
            ObjectiveUpsertRequest? request;
            try
            {
                request = args.HasValue ? JsonSerializer.Deserialize<ObjectiveUpsertRequest>(args.Value, _JsonOptions) : null;
            }
            catch (JsonException ex)
            {
                return new { Error = ex.Message, Code = code, Outcome = RecordWriteOutcomeEnum.Invalid, ValidEnums = BuildValidEnumMap() };
            }

            RecordWriteResult<Objective> result = await objectiveService.CreateRecordAsync(McpCallerContext.Require(), request).ConfigureAwait(false);
            return result.Succeeded ? result.Record! : WriteError(result, code, null, true);
        }

        private static async Task<object> UpdateAsync(ObjectiveService objectiveService, JsonElement? args, string code, string missingIdMessage, string? missingIdCode)
        {
            UpdateObjectiveArgs? request;
            try
            {
                request = args.HasValue ? JsonSerializer.Deserialize<UpdateObjectiveArgs>(args.Value, _JsonOptions) : null;
            }
            catch (JsonException ex)
            {
                return new { Error = ex.Message, Code = code, Outcome = RecordWriteOutcomeEnum.Invalid, ValidEnums = BuildValidEnumMap() };
            }

            string objectiveId = request?.ResolveObjectiveId() ?? String.Empty;
            if (String.IsNullOrWhiteSpace(objectiveId))
                return missingIdCode == null ? (object)new { Error = missingIdMessage } : new { Error = missingIdMessage, Code = missingIdCode };

            // The deserialized arguments are the upsert request itself, so every field the service reads,
            // autoDispatchEnabled included, reaches it without a hand-copied field list.
            RecordWriteResult<Objective> result = await objectiveService.UpdateRecordAsync(McpCallerContext.Require(), objectiveId, request).ConfigureAwait(false);
            return result.Succeeded ? result.Record! : WriteError(result, code, objectiveId, false);
        }

        private static object WriteError(RecordWriteResult<Objective> result, string code, string? objectiveId, bool includeEnums)
        {
            if (includeEnums)
                return new { Error = result.Message, Code = code, Outcome = result.Outcome, ObjectiveId = objectiveId, ValidEnums = BuildValidEnumMap() };
            return new { Error = result.Message, Code = code, Outcome = result.Outcome, ObjectiveId = objectiveId };
        }

        private static object BuildPreparationSchema()
        {
            object anchor = new
            {
                type = "object",
                properties = new
                {
                    vesselId = new { type = "string", description = "Vessel whose repository contains the revision" },
                    @ref = new { type = "string", description = "Human-readable ref that was resolved" },
                    resolvedCommit = new { type = "string", description = "Immutable commit resolved from the ref" }
                }
            };

            return new
            {
                type = "object",
                description = "Complete bounded repository preparation replacement. Send every source, target, and claim value that must remain. Changing an anchor marks only dependent claims for recheck.",
                properties = new
                {
                    requiredForDispatch = new { type = "boolean", description = "When true, dispatch requires immutable anchors and current evidence-backed required claims" },
                    requiredClaimKinds = new
                    {
                        type = "array",
                        items = new { type = "string", @enum = Enum.GetNames<ObjectivePreparationClaimKindEnum>() }
                    },
                    requiredSiblingInputs = new
                    {
                        type = "array",
                        maxItems = 20,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                vesselRef = new { type = "string", description = "Required sibling vessel ID or name" },
                                relativePath = new { type = "string", description = "Required checkout path relative to the target dock" },
                                requiredArtifactPaths = new { type = "array", maxItems = 20, items = new { type = "string" } }
                            },
                            required = new[] { "vesselRef", "relativePath" }
                        }
                    },
                    executionRequirements = new
                    {
                        type = "object",
                        description = "What the captain execution environment must provide. Names and paths only; never credentials or license material.",
                        properties = new
                        {
                            operatingSystem = new { type = "string", @enum = new[] { "Linux", "Windows", "MacOS" } },
                            architecture = new { type = "string", description = "Required process architecture, for example X64 or Arm64" },
                            executables = new { type = "array", maxItems = 20, items = new { type = "string" }, description = "Executables that must resolve on the captain PATH" },
                            dependencyPaths = new { type = "array", maxItems = 20, items = new { type = "string" }, description = "Files or directories the captain environment must read" },
                            isolationBoundary = new { type = "string", @enum = new[] { "Container", "Host" } },
                            licensedContext = new { type = "string", maxLength = 64, description = "Name of a licensed context captains must have available" }
                        }
                    },
                    source = anchor,
                    target = anchor,
                    claims = new
                    {
                        type = "array",
                        maxItems = 50,
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                id = new { type = "string", description = "Stable claim identifier" },
                                kind = new
                                {
                                    type = "string",
                                    @enum = Enum.GetNames<ObjectivePreparationClaimKindEnum>()
                                },
                                text = new { type = "string", maxLength = 2000 },
                                evidenceLinks = new { type = "array", maxItems = 20, items = new { type = "string" } },
                                dependsOn = new
                                {
                                    type = "string",
                                    @enum = Enum.GetNames<ObjectivePreparationDependencyEnum>()
                                },
                                state = new
                                {
                                    type = "string",
                                    @enum = Enum.GetNames<ObjectivePreparationClaimStateEnum>()
                                },
                                verifiedUtc = new { type = "string", description = "Last successful verification timestamp in UTC" },
                                invalidatedUtc = new { type = "string", description = "Anchor-change invalidation timestamp in UTC" },
                                invalidationReason = new { type = "string", maxLength = 1000 }
                            },
                            required = new[] { "id", "kind", "text" }
                        }
                    }
                }
            };
        }

        private static object BuildPlaybookSelectionSchema()
        {
            return new
            {
                type = "array",
                description = "Optional playbooks delivered to scheduler-created voyages",
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
            };
        }
    }
}
