namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Registers MCP tools for vessel CRUD operations.
    /// </summary>
    public static class McpVesselTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Registers vessel MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for vessel data access.</param>
        /// <param name="dockService">Optional dock service for worktree cleanup during vessel deletion.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, IDockService? dockService = null)
        {
            register(
                "armada_get_vessel",
                "Get details of a specific vessel (repository)",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" }
                    },
                    required = new[] { "vesselId" }
                },
                async (args) =>
                {
                    VesselIdArgs request = JsonSerializer.Deserialize<VesselIdArgs>(args!.Value, _JsonOptions)!;
                    string vesselId = request.VesselId;
                    Vessel? vessel = await database.Vessels.ReadAsync(vesselId).ConfigureAwait(false);
                    if (vessel == null) return (object)new { Error = "Vessel not found" };
                    return (object)vessel;
                });

            register(
                "armada_add_vessel",
                "Register a new vessel (git repository) in a fleet",
                new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string", description = "Display name for the vessel" },
                        repoUrl = new { type = "string", description = "Git repository URL (HTTPS or SSH)" },
                        fleetId = new { type = "string", description = "Fleet ID to add the vessel to" },
                        defaultBranch = new { type = "string", description = "Default branch name (defaults to main)" },
                        projectContext = new { type = "string", description = "Project context describing architecture, key files, and dependencies" },
                        styleGuide = new { type = "string", description = "Style guide describing naming conventions, patterns, and library preferences" },
                        workingDirectory = new { type = "string", description = "Optional local directory where completed mission changes will be pulled after merge" },
                        allowConcurrentMissions = new { type = "boolean", description = "Allow multiple concurrent missions on this vessel (default false)" },
                        enableModelContext = new { type = "boolean", description = "Enable legacy model context injection and learned-fact proposal routing for mission discoveries (default false)" },
                        defaultPipelineId = new { type = "string", description = "Default pipeline ID for dispatches to this vessel (ppl_ prefix)" },
                        protectedPaths = new
                        {
                            type = "array",
                            items = new { type = "string" },
                            description = "Optional additional glob patterns the captain must not modify. Armada always protects CLAUDE.md and _briefing before any merge or push."
                        },
                        autoLandPredicate = new
                        {
                            type = "object",
                            description = "Optional auto-land predicate config. Null = orchestrator-triggered lands only. Schema: { enabled: bool, maxAddedLines?: int, maxFiles?: int, allowPaths?: string[], denyPaths?: string[] }.",
                            additionalProperties = true
                        },
                        reflectionThreshold = new
                        {
                            type = "integer",
                            description = "Per-vessel override for the number of completed missions that triggers an auto-reflection. Null = use the global default (15). Must be >= 0; pass 0 only if you intend to disable auto-triggering."
                        },
                        reorganizeThreshold = new
                        {
                            type = "integer",
                            description = "Per-vessel override for the number of completed missions that triggers an auto-reorganize of the learned playbook. Null = use the global default. Must be a positive integer (>= 1)."
                        },
                        defaultPlaybooks = new
                        {
                            type = "array",
                            description = "Optional default playbooks merged into every dispatch against this vessel. Each entry: { playbookId: string, deliveryMode: string }. Omit to set no defaults.",
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
                        },
                        siblingRepos = new
                        {
                            type = "array",
                            description = "Optional dependency repositories provisioned alongside this vessel's worktree so cross-repo source probes resolve in a dock. Each entry: { vesselRef?: string, repoUrl?: string, relativePath: string, branchStrategy?: \"MatchBranchElseDefault\"|\"DefaultOnly\", defaultBranch?: string }. relativePath is resolved against the dock worktree (use \"../Name\" to place a sibling next to the dock). Omit to set none.",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    vesselRef = new { type = "string", description = "Optional known Armada vessel ID or name supplying the sibling source" },
                                    repoUrl = new { type = "string", description = "Optional git URL for the sibling source (used when vesselRef is unset/unresolvable)" },
                                    relativePath = new { type = "string", description = "Relative checkout path resolved against the dock worktree (e.g. ../ExampleSibling)" },
                                    branchStrategy = new { type = "string", description = "MatchBranchElseDefault (default) or DefaultOnly" },
                                    defaultBranch = new { type = "string", description = "Fallback base branch for the sibling; defaults to main" }
                                },
                                required = new[] { "relativePath" }
                            }
                        }
                    },
                    required = new[] { "name", "repoUrl", "fleetId" }
                },
                async (args) =>
                {
                    VesselAddArgs request = JsonSerializer.Deserialize<VesselAddArgs>(args!.Value, _JsonOptions)!;
                    string? autoLandPredicateJson = null;
                    if (args.HasValue && args.Value.TryGetProperty("autoLandPredicate", out JsonElement addAlpElem)
                        && addAlpElem.ValueKind != JsonValueKind.Null)
                    {
                        string addAlpRaw = addAlpElem.GetRawText();
                        try
                        {
                            JsonSerializer.Deserialize<Armada.Core.Models.AutoLandPredicate>(
                                addAlpRaw,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            autoLandPredicateJson = addAlpRaw;
                        }
                        catch (JsonException ex)
                        {
                            return (object)new { Error = "invalid autoLandPredicate JSON: " + ex.Message };
                        }
                    }
                    string? defaultPlaybooksJson = null;
                    if (args.HasValue && args.Value.TryGetProperty("defaultPlaybooks", out JsonElement addDpElem)
                        && addDpElem.ValueKind != JsonValueKind.Null)
                    {
                        string addDpRaw = addDpElem.GetRawText();
                        try
                        {
                            JsonSerializer.Deserialize<List<SelectedPlaybook>>(
                                addDpRaw,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            defaultPlaybooksJson = addDpRaw;
                        }
                        catch (JsonException ex)
                        {
                            return (object)new { Error = "invalid defaultPlaybooks JSON: " + ex.Message };
                        }
                    }
                    string? siblingReposJson = null;
                    if (args.HasValue && args.Value.TryGetProperty("siblingRepos", out JsonElement addSrElem)
                        && addSrElem.ValueKind != JsonValueKind.Null)
                    {
                        string addSrRaw = addSrElem.GetRawText();
                        try
                        {
                            List<Armada.Core.Models.SiblingRepo>? parsed = JsonSerializer.Deserialize<List<Armada.Core.Models.SiblingRepo>>(
                                addSrRaw,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            siblingReposJson = (parsed == null || parsed.Count == 0) ? null : addSrRaw;
                        }
                        catch (JsonException ex)
                        {
                            return (object)new { Error = "invalid siblingRepos JSON: " + ex.Message };
                        }
                    }
                    Vessel vessel = new Vessel();
                    vessel.TenantId = ArmadaConstants.DefaultTenantId;
                    vessel.Name = request.Name;
                    vessel.RepoUrl = request.RepoUrl;
                    vessel.FleetId = request.FleetId;
                    vessel.DefaultBranch = request.DefaultBranch ?? "main";
                    vessel.ProjectContext = request.ProjectContext;
                    vessel.StyleGuide = request.StyleGuide;
                    vessel.WorkingDirectory = request.WorkingDirectory;
                    vessel.AllowConcurrentMissions = request.AllowConcurrentMissions ?? false;
                    vessel.EnableModelContext = request.EnableModelContext ?? true;
                    vessel.DefaultPipelineId = request.DefaultPipelineId;
                    vessel.ProtectedPaths = (request.ProtectedPaths != null && request.ProtectedPaths.Count > 0) ? request.ProtectedPaths : null;
                    if (args.HasValue && args.Value.TryGetProperty("reflectionThreshold", out JsonElement addRtElem)
                        && addRtElem.ValueKind != JsonValueKind.Null)
                    {
                        if (addRtElem.ValueKind != JsonValueKind.Number || !addRtElem.TryGetInt32(out int addRtVal))
                            return (object)new { Error = "reflectionThreshold must be an integer" };
                        if (addRtVal < 0)
                            return (object)new { Error = "reflectionThreshold must not be negative" };
                        vessel.ReflectionThreshold = addRtVal;
                    }
                    if (args.HasValue && args.Value.TryGetProperty("reorganizeThreshold", out JsonElement addRorgElem)
                        && addRorgElem.ValueKind != JsonValueKind.Null)
                    {
                        if (addRorgElem.ValueKind != JsonValueKind.Number || !addRorgElem.TryGetInt32(out int addRorgVal))
                            return (object)new { Error = "reorganizeThreshold must be an integer" };
                        if (addRorgVal < 1)
                            return (object)new { Error = "reorganizeThreshold must be a positive integer" };
                        vessel.ReorganizeThreshold = addRorgVal;
                    }
                    vessel.AutoLandPredicate = autoLandPredicateJson;
                    vessel.DefaultPlaybooks = defaultPlaybooksJson;
                    vessel.SiblingRepos = siblingReposJson;
                    vessel = await database.Vessels.CreateAsync(vessel).ConfigureAwait(false);
                    return (object)vessel;
                });

            register(
                "armada_update_vessel",
                "Update an existing vessel's properties",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                        name = new { type = "string", description = "New display name" },
                        repoUrl = new { type = "string", description = "New repository URL" },
                        defaultBranch = new { type = "string", description = "New default branch" },
                        projectContext = new { type = "string", description = "New project context" },
                        styleGuide = new { type = "string", description = "New style guide" },
                        workingDirectory = new { type = "string", description = "New local directory where completed mission changes will be pulled after merge" },
                        localPath = new { type = "string", description = "New path to the local bare repository Armada cuts dock worktrees from. Set this when the bare repo is renamed or relocated (e.g. onto another drive); otherwise DockService keeps resolving the stale path and re-clones from repoUrl into it." },
                        allowConcurrentMissions = new { type = "boolean", description = "Allow multiple concurrent missions on this vessel" },
                        enableModelContext = new { type = "boolean", description = "Enable or disable legacy model context injection and learned-fact proposal routing" },
                        modelContext = new { type = "string", description = "Legacy model context retained for backward compatibility; mission discoveries should use [LEARNED-FACT-PROPOSAL]" },
                        defaultPipelineId = new { type = "string", description = "Default pipeline ID for dispatches to this vessel (ppl_ prefix)" },
                        protectedPaths = new
                        {
                            type = "array",
                            items = new { type = "string" },
                            description = "Additional glob patterns the captain must not modify. Armada always protects CLAUDE.md and _briefing. Pass an empty array to clear custom protection; omit the field to leave the existing list unchanged."
                        },
                        autoLandPredicate = new
                        {
                            type = "object",
                            description = "Optional auto-land predicate config. Null = orchestrator-triggered lands only. Schema: { enabled: bool, maxAddedLines?: int, maxFiles?: int, allowPaths?: string[], denyPaths?: string[] }. Omit to leave the existing predicate unchanged; pass null to clear it.",
                            additionalProperties = true
                        },
                        reflectionThreshold = new
                        {
                            type = "integer",
                            description = "Per-vessel reflection trigger threshold. Omit to leave unchanged; pass null to clear (revert to global default); pass an integer >= 0 to set."
                        },
                        reorganizeThreshold = new
                        {
                            type = "integer",
                            description = "Per-vessel reorganize trigger threshold. Omit to leave unchanged; pass null to clear (revert to global default); pass a positive integer (>= 1) to set."
                        },
                        defaultPlaybooks = new
                        {
                            type = "array",
                            description = "Default playbooks merged into every dispatch against this vessel. Each entry: { playbookId: string, deliveryMode: string }. Omit to leave unchanged; pass an empty array to clear all defaults; pass null to clear all defaults.",
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
                        },
                        siblingRepos = new
                        {
                            type = "array",
                            description = "Dependency repositories provisioned alongside this vessel's worktree so cross-repo source probes resolve in a dock. Each entry: { vesselRef?: string, repoUrl?: string, relativePath: string, branchStrategy?: \"MatchBranchElseDefault\"|\"DefaultOnly\", defaultBranch?: string }. relativePath is resolved against the dock worktree (use \"../Name\" to place a sibling next to the dock). Omit to leave unchanged; pass an empty array or null to clear all.",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    vesselRef = new { type = "string", description = "Optional known Armada vessel ID or name supplying the sibling source" },
                                    repoUrl = new { type = "string", description = "Optional git URL for the sibling source (used when vesselRef is unset/unresolvable)" },
                                    relativePath = new { type = "string", description = "Relative checkout path resolved against the dock worktree (e.g. ../ExampleSibling)" },
                                    branchStrategy = new { type = "string", description = "MatchBranchElseDefault (default) or DefaultOnly" },
                                    defaultBranch = new { type = "string", description = "Fallback base branch for the sibling; defaults to main" }
                                },
                                required = new[] { "relativePath" }
                            }
                        }
                    },
                    required = new[] { "vesselId" }
                },
                async (args) =>
                {
                    VesselUpdateArgs request = JsonSerializer.Deserialize<VesselUpdateArgs>(args!.Value, _JsonOptions)!;
                    string vesselId = request.VesselId;
                    if (!String.IsNullOrEmpty(request.ModelContext))
                        return (object)new { Error = "Direct modelContext mutation is blocked for captains. Emit a [CLAUDE.MD-PROPOSAL] block in your final response to propose learned-fact additions; the orchestrator applies approved proposals." };
                    Vessel? vessel = await database.Vessels.ReadAsync(vesselId).ConfigureAwait(false);
                    if (vessel == null) return (object)new { Error = "Vessel not found" };
                    if (request.Name != null)
                        vessel.Name = request.Name;
                    if (request.RepoUrl != null)
                        vessel.RepoUrl = request.RepoUrl;
                    if (request.DefaultBranch != null)
                        vessel.DefaultBranch = request.DefaultBranch;
                    if (request.ProjectContext != null)
                        vessel.ProjectContext = request.ProjectContext;
                    if (request.StyleGuide != null)
                        vessel.StyleGuide = request.StyleGuide;
                    if (request.WorkingDirectory != null)
                        vessel.WorkingDirectory = request.WorkingDirectory;
                    if (request.LocalPath != null)
                        vessel.LocalPath = request.LocalPath;
                    if (request.AllowConcurrentMissions.HasValue)
                        vessel.AllowConcurrentMissions = request.AllowConcurrentMissions.Value;
                    if (request.EnableModelContext.HasValue)
                        vessel.EnableModelContext = request.EnableModelContext.Value;
                    if (request.DefaultPipelineId != null)
                        vessel.DefaultPipelineId = request.DefaultPipelineId;
                    if (request.ProtectedPaths != null)
                        vessel.ProtectedPaths = request.ProtectedPaths.Count > 0 ? request.ProtectedPaths : null;
                    if (args.HasValue && args.Value.TryGetProperty("autoLandPredicate", out JsonElement updAlpElem))
                    {
                        if (updAlpElem.ValueKind == JsonValueKind.Null)
                        {
                            vessel.AutoLandPredicate = null;
                        }
                        else
                        {
                            string updAlpRaw = updAlpElem.GetRawText();
                            try
                            {
                                JsonSerializer.Deserialize<Armada.Core.Models.AutoLandPredicate>(
                                    updAlpRaw,
                                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                vessel.AutoLandPredicate = updAlpRaw;
                            }
                            catch (JsonException ex)
                            {
                                return (object)new { Error = "invalid autoLandPredicate JSON: " + ex.Message };
                            }
                        }
                    }
                    if (args.HasValue && args.Value.TryGetProperty("defaultPlaybooks", out JsonElement updDpElem))
                    {
                        if (updDpElem.ValueKind == JsonValueKind.Null)
                        {
                            vessel.DefaultPlaybooks = null;
                        }
                        else if (updDpElem.ValueKind == JsonValueKind.Array)
                        {
                            string updDpRaw = updDpElem.GetRawText();
                            try
                            {
                                List<SelectedPlaybook>? parsed = JsonSerializer.Deserialize<List<SelectedPlaybook>>(
                                    updDpRaw,
                                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                vessel.DefaultPlaybooks = (parsed == null || parsed.Count == 0) ? null : updDpRaw;
                            }
                            catch (JsonException ex)
                            {
                                return (object)new { Error = "invalid defaultPlaybooks JSON: " + ex.Message };
                            }
                        }
                    }
                    if (args.HasValue && args.Value.TryGetProperty("siblingRepos", out JsonElement updSrElem))
                    {
                        if (updSrElem.ValueKind == JsonValueKind.Null)
                        {
                            vessel.SiblingRepos = null;
                        }
                        else if (updSrElem.ValueKind == JsonValueKind.Array)
                        {
                            string updSrRaw = updSrElem.GetRawText();
                            try
                            {
                                List<Armada.Core.Models.SiblingRepo>? parsed = JsonSerializer.Deserialize<List<Armada.Core.Models.SiblingRepo>>(
                                    updSrRaw,
                                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                vessel.SiblingRepos = (parsed == null || parsed.Count == 0) ? null : updSrRaw;
                            }
                            catch (JsonException ex)
                            {
                                return (object)new { Error = "invalid siblingRepos JSON: " + ex.Message };
                            }
                        }
                    }
                    if (args.HasValue && args.Value.TryGetProperty("reflectionThreshold", out JsonElement updRtElem))
                    {
                        if (updRtElem.ValueKind == JsonValueKind.Null)
                        {
                            vessel.ReflectionThreshold = null;
                        }
                        else
                        {
                            if (updRtElem.ValueKind != JsonValueKind.Number || !updRtElem.TryGetInt32(out int updRtVal))
                                return (object)new { Error = "reflectionThreshold must be an integer" };
                            if (updRtVal < 0)
                                return (object)new { Error = "reflectionThreshold must not be negative" };
                            vessel.ReflectionThreshold = updRtVal;
                        }
                    }
                    if (args.HasValue && args.Value.TryGetProperty("reorganizeThreshold", out JsonElement updRorgElem))
                    {
                        if (updRorgElem.ValueKind == JsonValueKind.Null)
                        {
                            vessel.ReorganizeThreshold = null;
                        }
                        else
                        {
                            if (updRorgElem.ValueKind != JsonValueKind.Number || !updRorgElem.TryGetInt32(out int updRorgVal))
                                return (object)new { Error = "reorganizeThreshold must be an integer" };
                            if (updRorgVal < 1)
                                return (object)new { Error = "reorganizeThreshold must be a positive integer" };
                            vessel.ReorganizeThreshold = updRorgVal;
                        }
                    }
                    vessel = await database.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
                    return (object)vessel;
                });

            register(
                "armada_delete_vessel",
                "Delete a vessel by ID",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" }
                    },
                    required = new[] { "vesselId" }
                },
                async (args) =>
                {
                    VesselIdArgs request = JsonSerializer.Deserialize<VesselIdArgs>(args!.Value, _JsonOptions)!;
                    string vesselId = request.VesselId;
                    Vessel? vessel = await database.Vessels.ReadAsync(vesselId).ConfigureAwait(false);
                    if (vessel == null) return (object)new { Error = "Vessel not found" };

                    List<string> warnings = await CleanupVesselResourcesAsync(vessel, database, dockService).ConfigureAwait(false);

                    await database.Vessels.DeleteAsync(vesselId).ConfigureAwait(false);
                    if (warnings.Count > 0)
                        return (object)new { Status = "deleted", VesselId = vesselId, Warnings = warnings };
                    return (object)new { Status = "deleted", VesselId = vesselId };
                });

            register(
                "armada_delete_vessels",
                "Permanently delete multiple vessels from the database by ID. Returns a summary of deleted and skipped entries. This cannot be undone.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        ids = new { type = "array", items = new { type = "string" }, description = "List of vessel IDs to delete (vsl_ prefix)" }
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
                        Vessel? vessel = await database.Vessels.ReadAsync(id).ConfigureAwait(false);
                        if (vessel == null)
                        {
                            result.Skipped.Add(new DeleteMultipleSkipped(id, "Not found"));
                            continue;
                        }

                        await CleanupVesselResourcesAsync(vessel, database, dockService).ConfigureAwait(false);
                        await database.Vessels.DeleteAsync(id).ConfigureAwait(false);
                        result.Deleted++;
                    }
                    result.ResolveStatus();
                    return (object)result;
                });

            register(
                "armada_update_vessel_context",
                "Update a vessel's project context, style guide, or legacy model context without modifying other properties. Mission discoveries should be emitted as [LEARNED-FACT-PROPOSAL] instead of appended to modelContext.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Vessel ID (vsl_ prefix)" },
                        projectContext = new { type = "string", description = "Project context describing architecture, key files, and dependencies" },
                        styleGuide = new { type = "string", description = "Style guide describing naming conventions, patterns, and library preferences" },
                        modelContext = new { type = "string", description = "Legacy model context retained for backward compatibility; do not use for mission-discovered learned facts" }
                    },
                    required = new[] { "vesselId" }
                },
                async (args) =>
                {
                    VesselContextArgs request = JsonSerializer.Deserialize<VesselContextArgs>(args!.Value, _JsonOptions)!;
                    string vesselId = request.VesselId;
                    if (!String.IsNullOrEmpty(request.ModelContext))
                        return (object)new { Error = "Direct modelContext mutation is blocked for captains. Emit a [CLAUDE.MD-PROPOSAL] block in your final response to propose learned-fact additions; the orchestrator applies approved proposals." };
                    Vessel? vessel = await database.Vessels.ReadAsync(vesselId).ConfigureAwait(false);
                    if (vessel == null) return (object)new { Error = "Vessel not found" };
                    if (request.ProjectContext != null)
                        vessel.ProjectContext = request.ProjectContext;
                    if (request.StyleGuide != null)
                        vessel.StyleGuide = request.StyleGuide;
                    vessel = await database.Vessels.UpdateAsync(vessel).ConfigureAwait(false);
                    return (object)vessel;
                });
        }

        /// <summary>
        /// Cleans up ALL filesystem and database resources associated with a vessel before deletion.
        /// Cancels active missions, removes docks/worktrees, and deletes the bare repository.
        /// This method throws on failure -- vessel deletion should NOT proceed if cleanup fails.
        /// </summary>
        private static async Task<List<string>> CleanupVesselResourcesAsync(Vessel vessel, DatabaseDriver database, IDockService? dockService)
        {
            List<string> errors = new List<string>();

            // Cancel active missions on this vessel
            List<Mission> missions = await database.Missions.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
            foreach (Mission mission in missions)
            {
                if (mission.Status == Armada.Core.Enums.MissionStatusEnum.Pending
                    || mission.Status == Armada.Core.Enums.MissionStatusEnum.Assigned
                    || mission.Status == Armada.Core.Enums.MissionStatusEnum.InProgress
                    || mission.Status == Armada.Core.Enums.MissionStatusEnum.Review
                    || mission.Status == Armada.Core.Enums.MissionStatusEnum.Testing)
                {
                    mission.Status = Armada.Core.Enums.MissionStatusEnum.Cancelled;
                    mission.FailureReason = "Vessel deleted";
                    mission.CompletedUtc = DateTime.UtcNow;
                    mission.LastUpdateUtc = DateTime.UtcNow;
                    await database.Missions.UpdateAsync(mission).ConfigureAwait(false);
                }
            }

            // Delete ALL missions for this vessel from the database
            foreach (Mission mission in missions)
            {
                try { await database.Missions.DeleteAsync(mission.Id).ConfigureAwait(false); }
                catch (Exception ex) { errors.Add("Failed to delete mission " + mission.Id + ": " + ex.Message); }
            }

            // Clean up docks/worktrees for this vessel
            List<Dock> docks = await database.Docks.EnumerateByVesselAsync(vessel.Id).ConfigureAwait(false);
            foreach (Dock dock in docks)
            {
                try
                {
                    if (dockService != null)
                    {
                        await dockService.PurgeAsync(dock.Id).ConfigureAwait(false);
                    }
                    else
                    {
                        if (!String.IsNullOrEmpty(dock.WorktreePath) && Directory.Exists(dock.WorktreePath))
                            Directory.Delete(dock.WorktreePath, true);
                        await database.Docks.DeleteAsync(dock.Id).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) { errors.Add("Failed to purge dock " + dock.Id + ": " + ex.Message); }
            }

            // Delete the vessel's dock directory (the parent containing all worktrees)
            string vesselDockDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".armada", "docks", vessel.Name);
            if (Directory.Exists(vesselDockDir))
            {
                try { Directory.Delete(vesselDockDir, true); }
                catch (Exception ex) { errors.Add("Failed to delete dock directory " + vesselDockDir + ": " + ex.Message); }
            }

            // Delete the bare repo
            if (!String.IsNullOrEmpty(vessel.LocalPath) && Directory.Exists(vessel.LocalPath))
            {
                try { Directory.Delete(vessel.LocalPath, true); }
                catch (Exception ex) { errors.Add("Failed to delete bare repo " + vessel.LocalPath + ": " + ex.Message); }
            }

            // If the bare repo STILL exists after deletion attempt, that's a hard failure
            if (!String.IsNullOrEmpty(vessel.LocalPath) && Directory.Exists(vessel.LocalPath))
            {
                errors.Add("Bare repo still exists after deletion: " + vessel.LocalPath);
            }

            return errors;
        }
    }
}
