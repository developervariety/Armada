namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Registers MCP tools for voyage operations (dispatch, status, cancel, purge).
    /// </summary>
    public static class McpVoyageTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        /// <summary>
        /// Registers voyage MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for voyage data access.</param>
        /// <param name="admiral">Admiral service for voyage orchestration.</param>
        /// <param name="settings">Optional settings for log/diff cleanup during purge.</param>
        /// <param name="onStopCaptain">Optional callback that kills a captain's agent process by captain id.
        /// Invoked from armada_cancel_voyage when an in-flight mission is cancelled so the captain
        /// process actually exits instead of staying orphaned in Working state.</param>
        /// <param name="logging">Optional logging module. When provided it is used for downstream stage
        /// snapshot persistence; when null a silent fallback is created so snapshots are always persisted
        /// regardless of whether the caller threads logging in.</param>
        /// <param name="codeIndexService">Optional code index service used to auto-attach context packs.</param>
        /// <param name="objectiveService">Optional objective service used to validate and link voyage scope.</param>
        /// <remarks>
        /// armada_dispatch accepts an optional <c>prestagedFiles</c> array on each
        /// mission entry. Each entry copies an absolute <c>sourcePath</c> on the
        /// Admiral host into a relative <c>destPath</c> within the dock worktree
        /// before the captain process is launched. Useful for handing the captain
        /// a context snapshot, generated test fixture, or one-shot input file
        /// that should not be committed to the vessel repository.
        /// </remarks>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            IAdmiralService admiral,
            ArmadaSettings? settings = null,
            Func<string, Task>? onStopCaptain = null,
            LoggingModule? logging = null,
            ICodeIndexService? codeIndexService = null,
            ObjectiveService? objectiveService = null,
            LongRunningJobService? jobs = null)
        {
            register(
                "armada_dispatch",
                "Dispatch a new voyage with missions to a vessel. Link objectiveId for non-trivial work so the objective/backlog item carries scope and evidence lineage. Each mission may include an optional prestagedFiles array of {sourcePath, destPath} entries; the Admiral copies sourcePath (absolute, on the Admiral host) into destPath (relative, inside the dock worktree) after the dock is created and before the captain spawns. Code-index context packs are attached by default when available; set codeContextMode to off to opt out or force to require generation. Each mission may use preferredModel with a complexity tier: low, mid, or high.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string", description = "Voyage title" },
                        description = new { type = "string", description = "Voyage description" },
                        vesselId = new { type = "string", description = "Target vessel ID" },
                        missions = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    title = new { type = "string" },
                                    description = new { type = "string" },
                                    prestagedFiles = new
                                    {
                                        type = "array",
                                        description = "Optional file-copy operations executed by the Admiral after the dock is created and before the captain is launched. Max 50 entries totalling 50 MB per mission.",
                                        items = new
                                        {
                                            type = "object",
                                            properties = new
                                            {
                                                sourcePath = new { type = "string", description = "Absolute path on the Admiral host" },
                                                destPath = new { type = "string", description = "Relative path within the dock worktree (no '..' segments)" }
                                            },
                                            required = new[] { "sourcePath", "destPath" }
                                        }
                                    },
                                    codeContextMode = new { type = "string", description = "Optional per-mission code context mode: auto, off, or force. Overrides the dispatch-level codeContextMode." },
                                    codeContextQuery = new { type = "string", description = "Optional per-mission code search query. Defaults to mission title plus description." },
                                    preferredModel = new { type = "string", description = "Optional complexity tier. Use 'low', 'mid', or 'high'; Armada picks an available model within that tier. Omit when default routing is sufficient." },
                                    capabilityHint = new { type = "string", description = "Optional capability hint for within-tier selection: audit, reasoning-heavy, mechanical, or doc-only. Armada prefers the best-fit available captain in the chosen tier; omit when default routing is sufficient." },
                                    dependsOnMissionId = new { type = "string", description = "Optional mission ID (msn_ prefix) this mission must wait for. The dependent mission stays Pending until the referenced mission reaches a completion state." },
                                    alias = new { type = "string", description = "Optional logical alias for this mission within this dispatch batch (e.g. 'M1', 'resolver'). Other missions in the same batch may reference it via dependsOnMissionAlias. Must be unique within the batch." },
                                    dependsOnMissionAlias = new { type = "string", description = "Optional alias of another mission in this same dispatch batch that this mission must wait for. The server resolves the alias to the concrete msn_* ID after creating the dependency mission. Takes precedence over dependsOnMissionId when both are supplied." },
                                    selectedPlaybooks = new
                                    {
                                        type = "array",
                                        description = "Optional per-mission playbook selections merged with voyage-level selectedPlaybooks (same semantics as vessel default merge)",
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
                                }
                            }
                        },
                        codeContextMode = new { type = "string", description = "Code context mode for this dispatch: auto (default), off, or force. Mission-level codeContextMode overrides this value." },
                        codeContextTokenBudget = new { type = "integer", description = "Optional token budget for each generated context pack. Defaults to a conservative 5000 tokens." },
                        codeContextMaxResults = new { type = "integer", description = "Optional maximum number of code-index evidence results per context pack. Omit to use CodeIndex settings." },
                        pipelineId = new { type = "string", description = "Pipeline ID to use for this dispatch (overrides vessel/fleet default)" },
                        pipeline = new { type = "string", description = "Pipeline name to use (convenience alias for pipelineId -- resolves by name)" },
                        objectiveId = new { type = "string", description = "Optional objective/backlog item ID (obj_ prefix) to link to the dispatched voyage" },
                        selectedPlaybooks = new
                        {
                            type = "array",
                            description = "Ordered playbooks to apply during dispatch",
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
                    required = new[] { "title", "vesselId", "missions" }
                },
                async (args) =>
                {
                    logging?.Debug("[MCP-PROBE] armada_dispatch handler ENTER argsPresent=" + args.HasValue);
                    VoyageDispatchArgs request = JsonSerializer.Deserialize<VoyageDispatchArgs>(args!.Value, _JsonOptions)!;
                    logging?.Debug("[MCP-PROBE] armada_dispatch DESERIALIZED title='" + (request.Title ?? "?") + "' descLen=" + (request.Description?.Length ?? 0) + " missions=" + (request.Missions?.Count ?? 0));
                    SharedVoyageDispatchRequest dispatchRequest = new SharedVoyageDispatchRequest
                    {
                        Title = request.Title,
                        Description = request.Description ?? "",
                        VesselId = request.VesselId,
                        Missions = request.Missions,
                        CodeContextMode = request.CodeContextMode,
                        CodeContextTokenBudget = request.CodeContextTokenBudget,
                        CodeContextMaxResults = request.CodeContextMaxResults,
                        PipelineId = request.PipelineId,
                        Pipeline = request.Pipeline,
                        ObjectiveId = request.ObjectiveId,
                        SelectedPlaybooks = request.SelectedPlaybooks ?? new List<SelectedPlaybook>(),
                        Settings = settings
                    };
                    VoyageDispatchService dispatchService = new VoyageDispatchService(
                        database,
                        admiral,
                        logging,
                        codeIndexService,
                        objectiveService,
                        settings);

                    if (jobs != null)
                    {
                        // Validate synchronously so a bad request still fails fast with its specific
                        // code (vessel_not_found, pipeline_not_found, ...) rather than being accepted
                        // as a job. Only the expensive tail -- per-mission context packs, dock
                        // creation, captain spawn -- runs in the background, so a client deadline can
                        // no longer turn a live dispatch into an indistinguishable internal error.
                        VoyageDispatchResult? invalid = await dispatchService
                            .ValidatePreconditionsAsync(dispatchRequest).ConfigureAwait(false);
                        if (invalid != null) return invalid.Value;

                        return (object)jobs.Start(
                            "voyage_dispatch",
                            async (token) => (object?)(await dispatchService
                                .DispatchAsync(dispatchRequest, token).ConfigureAwait(false)).Value);
                    }

                    VoyageDispatchResult result = await dispatchService.DispatchAsync(dispatchRequest).ConfigureAwait(false);
                    return result.Value;
                });

            register(
                "armada_voyage_status",
                "Get status of a specific voyage. Returns summary with mission counts by default; opt in to compact mission details with includeMissions/includeFields.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        voyageId = new { type = "string", description = "Voyage ID (vyg_ prefix)" },
                        summary = new { type = "boolean", description = "Return summary only with mission counts by status (default true). Set false to include mission objects." },
                        includeMissions = new { type = "boolean", description = "Include full mission objects (default false). Only used when summary=false." },
                        includeDescription = new { type = "boolean", description = "Include Description on embedded missions (default false)" },
                        includeDiffs = new { type = "boolean", description = "Include saved diff for each mission (default false)" },
                        includeLogs = new { type = "boolean", description = "Include log excerpt for each mission (default false). Currently reserved for future use." },
                        includeFields = new { type = "array", items = new { type = "string" }, description = "Optional mission fields: titles, statuses, commitHashes, dependsOn, prestaged, persona, model" }
                    },
                    required = new[] { "voyageId" }
                },
                async (args) =>
                {
                    VoyageStatusArgs request = JsonSerializer.Deserialize<VoyageStatusArgs>(args!.Value, _JsonOptions)!;
                    string voyageId = request.VoyageId;
                    Voyage? voyage = await database.Voyages.ReadAsync(voyageId).ConfigureAwait(false);
                    if (voyage == null) return (object)new { Error = "Voyage not found" };

                    // Default: summary mode (returns voyage metadata + mission counts by status, no mission objects)
                    bool isSummary = request.Summary != false;
                    if (isSummary)
                    {
                        Dictionary<MissionStatusEnum, int> statusCounts = await database.Missions.CountByVoyageStatusAsync(voyageId).ConfigureAwait(false);
                        Dictionary<string, int> counts = statusCounts.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value);
                        EnumerationQuery summaryAssignmentQuery = new EnumerationQuery
                        {
                            VoyageId = voyageId,
                            PageSize = 1000
                        };
                        List<Mission> summaryMissions = (await database.Missions.EnumerateSummariesAsync(summaryAssignmentQuery).ConfigureAwait(false)).Objects;
                        Dictionary<string, int> assignmentCounts = summaryMissions
                            .GroupBy(m => m.AssignmentState.ToString())
                            .ToDictionary(g => g.Key, g => g.Count());
                        return (object)new
                        {
                            Voyage = new { voyage.Id, voyage.Title, voyage.Description, voyage.Status, voyage.CreatedUtc, voyage.LastUpdateUtc },
                            TotalMissions = counts.Values.Sum(),
                            MissionCountsByStatus = counts,
                            MissionCountsByAssignmentState = assignmentCounts
                        };
                    }

                    EnumerationQuery missionQuery = new EnumerationQuery
                    {
                        VoyageId = voyageId,
                        PageSize = 1000
                    };
                    List<Mission> missionSummaries = (await database.Missions.EnumerateSummariesAsync(missionQuery).ConfigureAwait(false)).Objects;

                    // Non-summary: optionally include mission objects
                    if (request.IncludeMissions != true)
                    {
                        return (object)new { Voyage = voyage, TotalMissions = missionSummaries.Count };
                    }

                    List<Mission> missions = await database.Missions.EnumerateByVoyageAsync(voyageId).ConfigureAwait(false);
                    return (object)new
                    {
                        Voyage = new { voyage.Id, voyage.Title, voyage.Status, voyage.CreatedUtc, voyage.CompletedUtc, voyage.LastUpdateUtc, DescriptionLength = voyage.Description?.Length ?? 0 },
                        Missions = missions.Select(m => BuildSlimMissionStatus(m, request.IncludeFields)).ToList()
                    };
                });

            register(
                "armada_cancel_voyage",
                "Cancel an entire voyage and all its pending missions",
                new
                {
                    type = "object",
                    properties = new
                    {
                        voyageId = new { type = "string", description = "Voyage ID (vyg_ prefix)" }
                    },
                    required = new[] { "voyageId" }
                },
                async (args) =>
                {
                    VoyageIdArgs request = JsonSerializer.Deserialize<VoyageIdArgs>(args!.Value, _JsonOptions)!;
                    string voyageId = request.VoyageId;
                    Voyage? voyage = await database.Voyages.ReadAsync(voyageId).ConfigureAwait(false);
                    if (voyage == null) return (object)new { Error = "Voyage not found" };

                    // Cancel pending/assigned/in-progress missions. In-progress missions have a
                    // running captain process that must be killed; otherwise the captain stays
                    // Working forever and blocks the dispatcher from assigning new missions to
                    // that captain or that single-captain pool. The same teardown applies to
                    // Assigned-with-captain missions whose process started but didn't yet flip
                    // the mission to InProgress.
                    List<Mission> missions = await database.Missions.EnumerateByVoyageAsync(voyageId).ConfigureAwait(false);
                    int cancelledCount = 0;
                    foreach (Mission m in missions)
                    {
                        bool isCancellable = m.Status == MissionStatusEnum.Pending
                            || m.Status == MissionStatusEnum.Assigned
                            || m.Status == MissionStatusEnum.InProgress;
                        if (!isCancellable) continue;

                        // Release the captain if this mission was assigned to one. Only kill the
                        // process when the captain is currently running THIS mission; if the
                        // captain has moved on, leave it alone.
                        if (!String.IsNullOrEmpty(m.CaptainId))
                        {
                            Captain? captain = await database.Captains.ReadAsync(m.CaptainId).ConfigureAwait(false);
                            if (captain != null && captain.CurrentMissionId == m.Id)
                            {
                                List<Mission> otherMissions = (await database.Missions.EnumerateByCaptainAsync(captain.Id).ConfigureAwait(false))
                                    .Where(om => om.Id != m.Id && (om.Status == MissionStatusEnum.InProgress || om.Status == MissionStatusEnum.Assigned)).ToList();
                                if (otherMissions.Count == 0)
                                {
                                    // Kill the running agent process FIRST so it doesn't try to
                                    // commit / push / mutate state under the cancelled mission.
                                    // RecallCaptainAsync resets DB state to Idle.
                                    if (onStopCaptain != null)
                                    {
                                        try { await onStopCaptain(captain.Id).ConfigureAwait(false); }
                                        catch { /* best-effort; we still want to reset DB state */ }
                                    }
                                    try { await admiral.RecallCaptainAsync(captain.Id).ConfigureAwait(false); }
                                    catch
                                    {
                                        // Fall back to direct DB reset if Admiral recall blew up.
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

                        m.Status = MissionStatusEnum.Cancelled;
                        m.ProcessId = null;
                        m.CompletedUtc = DateTime.UtcNow;
                        m.LastUpdateUtc = DateTime.UtcNow;
                        await database.Missions.UpdateAsync(m).ConfigureAwait(false);
                        cancelledCount++;
                    }

                    voyage.Status = VoyageStatusEnum.Cancelled;
                    voyage.CompletedUtc = DateTime.UtcNow;
                    voyage.LastUpdateUtc = DateTime.UtcNow;
                    voyage = await database.Voyages.UpdateAsync(voyage).ConfigureAwait(false);
                    return (object)new { Voyage = voyage, CancelledMissions = cancelledCount };
                });

            register(
                "armada_purge_voyage",
                "Permanently delete a voyage and all its missions from the database. This cannot be undone.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        voyageId = new { type = "string", description = "Voyage ID (vyg_ prefix)" }
                    },
                    required = new[] { "voyageId" }
                },
                async (args) =>
                {
                    VoyageIdArgs request = JsonSerializer.Deserialize<VoyageIdArgs>(args!.Value, _JsonOptions)!;
                    string voyageId = request.VoyageId;
                    Voyage? voyage = await database.Voyages.ReadAsync(voyageId).ConfigureAwait(false);
                    if (voyage == null) return (object)new { Error = "Voyage not found" };

                    // Block deletion of active voyages
                    if (voyage.Status == VoyageStatusEnum.Open || voyage.Status == VoyageStatusEnum.InProgress)
                        return (object)new { Error = "Cannot delete voyage while status is " + voyage.Status + ". Cancel the voyage first." };

                    List<Mission> missions = await database.Missions.EnumerateByVoyageAsync(voyageId).ConfigureAwait(false);

                    // Block deletion if any missions are actively assigned or in progress
                    int activeMissionCount = missions.Count(m => m.Status == MissionStatusEnum.Assigned || m.Status == MissionStatusEnum.InProgress);
                    if (activeMissionCount > 0)
                        return (object)new { Error = "Cannot delete voyage with " + activeMissionCount + " active mission(s) in Assigned or InProgress status. Cancel or complete them first." };

                    foreach (Mission m in missions)
                    {
                        await CleanupMissionResourcesAsync(m, database, settings).ConfigureAwait(false);
                        await database.Missions.DeleteAsync(m.Id).ConfigureAwait(false);
                    }

                    await database.Voyages.DeleteAsync(voyageId).ConfigureAwait(false);
                    return (object)new { Status = "deleted", VoyageId = voyageId, MissionsDeleted = missions.Count };
                });

            register(
                "armada_delete_voyages",
                "Permanently delete multiple voyages and their associated missions from the database by ID. Voyages that are Open/InProgress or have active missions are skipped. Returns a summary of deleted and skipped entries. This cannot be undone.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        ids = new { type = "array", items = new { type = "string" }, description = "List of voyage IDs to delete (vyg_ prefix)" }
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
                        Voyage? voyage = await database.Voyages.ReadAsync(id).ConfigureAwait(false);
                        if (voyage == null)
                        {
                            result.Skipped.Add(new DeleteMultipleSkipped(id, "Not found"));
                            continue;
                        }
                        if (voyage.Status == VoyageStatusEnum.Open || voyage.Status == VoyageStatusEnum.InProgress)
                        {
                            result.Skipped.Add(new DeleteMultipleSkipped(id, "Cannot delete voyage while status is " + voyage.Status + ". Cancel the voyage first."));
                            continue;
                        }
                        List<Mission> missions = await database.Missions.EnumerateByVoyageAsync(id).ConfigureAwait(false);
                        int activeMissionCount = missions.Count(m => m.Status == MissionStatusEnum.Assigned || m.Status == MissionStatusEnum.InProgress);
                        if (activeMissionCount > 0)
                        {
                            result.Skipped.Add(new DeleteMultipleSkipped(id, "Cannot delete voyage with " + activeMissionCount + " active mission(s). Cancel or complete them first."));
                            continue;
                        }
                        foreach (Mission m in missions)
                        {
                            await CleanupMissionResourcesAsync(m, database, settings).ConfigureAwait(false);
                            await database.Missions.DeleteAsync(m.Id).ConfigureAwait(false);
                        }
                        await database.Voyages.DeleteAsync(id).ConfigureAwait(false);
                        result.Deleted++;
                    }
                    result.ResolveStatus();
                    return (object)result;
                });
        }

        /// <summary>
        /// Cleans up filesystem resources associated with a mission (dock/worktree, log files, diff files).
        /// Cleanup failures are silently caught to avoid blocking the mission delete.
        /// </summary>
        /// <param name="mission">The mission being deleted.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Optional settings for log/diff paths.</param>
        private static async Task CleanupMissionResourcesAsync(Mission mission, DatabaseDriver database, ArmadaSettings? settings)
        {
            // Clean up associated dock/worktree
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

            // Clean up log and diff files
            if (settings != null)
            {
                try
                {
                    string logPath = Path.Combine(settings.LogDirectory, "missions", mission.Id + ".log");
                    if (File.Exists(logPath)) File.Delete(logPath);
                }
                catch { }
                try
                {
                    string diffPath = Path.Combine(settings.LogDirectory, "diffs", mission.Id + ".diff");
                    if (File.Exists(diffPath)) File.Delete(diffPath);
                }
                catch { }
            }
        }

        private static object BuildSlimMissionStatus(Mission mission, List<string>? includeFields)
        {
            HashSet<string> fields = new HashSet<string>(includeFields ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            bool defaultSlim = fields.Count == 0;
            return new
            {
                mission.Id,
                Title = defaultSlim || fields.Contains("titles") ? mission.Title : null,
                Status = defaultSlim || fields.Contains("statuses") ? mission.Status : (MissionStatusEnum?)null,
                Persona = defaultSlim || fields.Contains("persona") ? mission.Persona : null,
                PreferredModel = defaultSlim || fields.Contains("model") ? mission.PreferredModel : null,
                CommitHash = defaultSlim || fields.Contains("commitHashes") ? mission.CommitHash : null,
                DependsOnMissionId = defaultSlim || fields.Contains("dependsOn") ? mission.DependsOnMissionId : null,
                PrestagedFiles = fields.Contains("prestaged") ? mission.PrestagedFiles : null,
                mission.AssignmentState,
                mission.CaptainId,
                mission.ProcessId,
                mission.DockId,
                mission.BranchName,
                mission.FailureReason,
                mission.CreatedUtc,
                mission.LastUpdateUtc,
                mission.StartedUtc,
                mission.CompletedUtc,
                Description = TruncateForStatus(mission.Description, 4096),
                AgentOutput = TruncateForStatus(mission.AgentOutput, 4096)
            };
        }

        private static object? TruncateForStatus(string? value, int maxChars)
        {
            if (String.IsNullOrEmpty(value)) return null;
            if (value.Length <= maxChars) return new { Text = value, Truncated = false, FullLength = value.Length };
            return new { Text = value.Substring(0, maxChars), Truncated = true, FullLength = value.Length };
        }
    }
}
