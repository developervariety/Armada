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
        private const string CodeContextDestPath = "_briefing/context-pack.md";
        private const string CodeContextModeAuto = "auto";
        private const string CodeContextModeOff = "off";
        private const string CodeContextModeForce = "force";
        private const int DefaultCodeContextTokenBudget = 3000;

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
            ICodeIndexService? codeIndexService = null)
        {
            register(
                "armada_dispatch",
                "Dispatch a new voyage with missions to a vessel. Each mission may include an optional prestagedFiles array of {sourcePath, destPath} entries; the Admiral copies sourcePath (absolute, on the Admiral host) into destPath (relative, inside the dock worktree) after the dock is created and before the captain spawns. Code-index context packs are attached by default when available; set codeContextMode to off to opt out or force to require generation. Each mission may use preferredModel with a complexity tier: low, mid, or high.",
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
                        codeContextTokenBudget = new { type = "integer", description = "Optional token budget for each generated context pack. Defaults to a conservative 3000 tokens." },
                        codeContextMaxResults = new { type = "integer", description = "Optional maximum number of code-index evidence results per context pack. Omit to use CodeIndex settings." },
                        pipelineId = new { type = "string", description = "Pipeline ID to use for this dispatch (overrides vessel/fleet default)" },
                        pipeline = new { type = "string", description = "Pipeline name to use (convenience alias for pipelineId -- resolves by name)" },
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
                    VoyageDispatchArgs request = JsonSerializer.Deserialize<VoyageDispatchArgs>(args!.Value, _JsonOptions)!;
                    string title = request.Title;
                    string description = request.Description ?? "";
                    string vesselId = request.VesselId;
                    List<MissionDescription> missions = request.Missions;
                    List<SelectedPlaybook> callerPlaybooks = request.SelectedPlaybooks ?? new List<SelectedPlaybook>();

                    // Merge vessel DefaultPlaybooks with caller-supplied selectedPlaybooks.
                    // Start with the vessel defaults; caller entries override deliveryMode on collision and append new entries.
                    Vessel? dispatchVessel = await database.Vessels.ReadAsync(vesselId).ConfigureAwait(false);
                    List<SelectedPlaybook> mergedPlaybooks = MergePlaybooks(dispatchVessel?.GetDefaultPlaybooks(), callerPlaybooks);

                    // Resolve pipeline name to ID up front so both the alias path and
                    // the standard path see the same identifier.
                    string? pipelineId = request.PipelineId;
                    if (String.IsNullOrEmpty(pipelineId) && !String.IsNullOrEmpty(request.Pipeline))
                    {
                        Pipeline? namedPipeline = await database.Pipelines.ReadByNameAsync(request.Pipeline).ConfigureAwait(false);
                        if (namedPipeline != null) pipelineId = namedPipeline.Id;
                        else return (object)new { Error = "Pipeline not found: " + request.Pipeline };
                    }

                    string? codeContextError = await ApplyDispatchCodeContextAsync(
                        codeIndexService,
                        logging,
                        vesselId,
                        request.CodeContextMode,
                        request.CodeContextTokenBudget,
                        request.CodeContextMaxResults,
                        missions).ConfigureAwait(false);
                    if (codeContextError != null) return (object)new { Error = codeContextError };

                    // When any mission carries an alias or dependsOnMissionAlias, use the
                    // alias-aware dispatch path which topologically sorts missions and
                    // resolves alias references to concrete msn_* IDs at creation time.
                    bool hasAliases = missions.Any(m =>
                        !String.IsNullOrEmpty(m.Alias) || !String.IsNullOrEmpty(m.DependsOnMissionAlias));
                    if (hasAliases)
                    {
                        return await DispatchWithAliasesAsync(
                            database, admiral, logging, title, description, vesselId,
                            dispatchVessel, missions, mergedPlaybooks, pipelineId).ConfigureAwait(false);
                    }

                    Voyage voyage = await admiral.DispatchVoyageAsync(title, description, vesselId, missions, pipelineId, mergedPlaybooks).ConfigureAwait(false);
                    return (object)voyage;
                });

            register(
                "armada_voyage_status",
                "Get status of a specific voyage. Returns summary with mission counts by default; opt-in to full mission details.",
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
                        includeLogs = new { type = "boolean", description = "Include log excerpt for each mission (default false). Currently reserved for future use." }
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
                        return (object)new
                        {
                            Voyage = new { voyage.Id, voyage.Title, voyage.Description, voyage.Status, voyage.CreatedUtc, voyage.LastUpdateUtc },
                            TotalMissions = counts.Values.Sum(),
                            MissionCountsByStatus = counts
                        };
                    }

                    EnumerationQuery missionQuery = new EnumerationQuery
                    {
                        VoyageId = voyageId,
                        PageSize = 1000
                    };
                    List<Mission> missions = (await database.Missions.EnumerateSummariesAsync(missionQuery).ConfigureAwait(false)).Objects;

                    // Non-summary: optionally include mission objects
                    if (request.IncludeMissions != true)
                    {
                        return (object)new { Voyage = voyage, TotalMissions = missions.Count };
                    }

                    // Mission objects are lightweight summaries. Logs and diffs are available through mission-specific tools.
                    return (object)new { Voyage = voyage, Missions = missions };
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

        private static async Task<string?> ApplyDispatchCodeContextAsync(
            ICodeIndexService? codeIndexService,
            LoggingModule? logging,
            string vesselId,
            string? topLevelMode,
            int? tokenBudget,
            int? maxResults,
            List<MissionDescription> missions)
        {
            if (missions == null || missions.Count == 0) return null;

            string dispatchMode;
            if (!TryNormalizeCodeContextMode(topLevelMode, CodeContextModeAuto, out dispatchMode))
                return "invalid codeContextMode: " + topLevelMode + ". Expected auto, off, or force.";

            bool loggedUnavailable = false;
            for (int i = 0; i < missions.Count; i++)
            {
                MissionDescription mission = missions[i];
                if (mission == null) continue;

                string mode;
                if (!TryNormalizeCodeContextMode(mission.CodeContextMode, dispatchMode, out mode))
                    return "invalid codeContextMode for mission '" + mission.Title + "': " + mission.CodeContextMode + ". Expected auto, off, or force.";

                if (String.Equals(mode, CodeContextModeOff, StringComparison.Ordinal))
                    continue;

                string query = BuildMissionCodeContextQuery(mission);
                if (String.IsNullOrWhiteSpace(query))
                {
                    if (String.Equals(mode, CodeContextModeForce, StringComparison.Ordinal))
                        return "code context force requested for mission '" + mission.Title + "' but no query could be built";

                    LogCodeContextWarning(logging, "skipping code context for mission '" + mission.Title + "' because no query could be built");
                    continue;
                }

                if (codeIndexService == null)
                {
                    if (String.Equals(mode, CodeContextModeForce, StringComparison.Ordinal))
                        return "code context force requested but code index service is unavailable";

                    if (!loggedUnavailable)
                    {
                        LogCodeContextWarning(logging, "code index service is unavailable; dispatch will continue without auto code context");
                        loggedUnavailable = true;
                    }
                    continue;
                }

                ContextPackRequest contextRequest = new ContextPackRequest
                {
                    VesselId = vesselId,
                    Goal = query,
                    TokenBudget = tokenBudget ?? DefaultCodeContextTokenBudget,
                    MaxResults = maxResults
                };

                try
                {
                    ContextPackResponse contextPack = await codeIndexService
                        .BuildContextPackAsync(contextRequest)
                        .ConfigureAwait(false);

                    if (contextPack.PrestagedFiles == null || contextPack.PrestagedFiles.Count == 0)
                    {
                        if (String.Equals(mode, CodeContextModeForce, StringComparison.Ordinal))
                            return "code context generation returned no prestaged files for mission '" + mission.Title + "'";

                        LogCodeContextWarning(logging, "code context generation returned no prestaged files for mission '" + mission.Title + "'");
                        continue;
                    }

                    MergeGeneratedPrestagedFiles(mission, contextPack.PrestagedFiles, logging);
                }
                catch (Exception ex)
                {
                    if (String.Equals(mode, CodeContextModeForce, StringComparison.Ordinal))
                        return "code context generation failed for mission '" + mission.Title + "': " + ex.Message;

                    LogCodeContextWarning(logging, "code context generation failed for mission '" + mission.Title + "': " + ex.Message);
                }
            }

            return null;
        }

        private static bool TryNormalizeCodeContextMode(string? value, string fallback, out string normalized)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                normalized = fallback;
                return true;
            }

            string candidate = value.Trim().ToLowerInvariant();
            if (String.Equals(candidate, CodeContextModeAuto, StringComparison.Ordinal)
                || String.Equals(candidate, CodeContextModeOff, StringComparison.Ordinal)
                || String.Equals(candidate, CodeContextModeForce, StringComparison.Ordinal))
            {
                normalized = candidate;
                return true;
            }

            normalized = fallback;
            return false;
        }

        private static string BuildMissionCodeContextQuery(MissionDescription mission)
        {
            if (!String.IsNullOrWhiteSpace(mission.CodeContextQuery))
                return mission.CodeContextQuery.Trim();

            string title = mission.Title ?? "";
            string description = mission.Description ?? "";
            if (String.IsNullOrWhiteSpace(description)) return title.Trim();
            if (String.IsNullOrWhiteSpace(title)) return description.Trim();
            return title.Trim() + "\n\n" + description.Trim();
        }

        private static void MergeGeneratedPrestagedFiles(
            MissionDescription mission,
            List<PrestagedFile> generatedFiles,
            LoggingModule? logging)
        {
            if (generatedFiles == null || generatedFiles.Count == 0) return;

            List<PrestagedFile> merged = mission.PrestagedFiles ?? new List<PrestagedFile>();
            foreach (PrestagedFile generated in generatedFiles)
            {
                if (generated == null) continue;

                bool duplicateDest = false;
                foreach (PrestagedFile existing in merged)
                {
                    if (existing == null) continue;
                    if (String.Equals(existing.DestPath, generated.DestPath, StringComparison.Ordinal))
                    {
                        duplicateDest = true;
                        break;
                    }
                }

                if (duplicateDest)
                {
                    LogCodeContextWarning(logging, "skipping generated code context prestaged file because destPath already exists: " + generated.DestPath);
                    continue;
                }

                merged.Add(new PrestagedFile(generated.SourcePath ?? "", generated.DestPath ?? CodeContextDestPath));
            }

            mission.PrestagedFiles = merged.Count > 0 ? merged : null;
        }

        private static void LogCodeContextWarning(LoggingModule? logging, string message)
        {
            if (logging == null) return;
            logging.Warn("[McpVoyageTools] " + message);
        }

        /// <summary>
        /// Dispatch handler for missions that use logical alias-based
        /// inter-mission dependencies. Validates and topologically sorts the
        /// alias graph, creates a single voyage, then creates each mission in
        /// dependency order -- resolving alias references to concrete msn_* IDs
        /// as each mission is persisted. When a multi-stage pipeline applies,
        /// each MissionDescription expands into a chain of stage missions; the
        /// alias maps to the LAST stage's mission id so downstream alias deps
        /// wait for the entire review chain to complete.
        /// </summary>
        private static async Task<object> DispatchWithAliasesAsync(
            DatabaseDriver database,
            IAdmiralService admiral,
            LoggingModule? logging,
            string title,
            string description,
            string vesselId,
            Vessel? vessel,
            List<MissionDescription> missions,
            List<SelectedPlaybook> selectedPlaybooks,
            string? pipelineId)
        {
            if (vessel == null)
                return new { Error = "Vessel not found: " + vesselId };

            // Validate alias uniqueness + dependency references and return missions
            // in topological creation order (dependencies before dependents).
            IReadOnlyList<MissionDescription> sortedMissions;
            try
            {
                sortedMissions = MissionAliasResolver.ResolveAndOrder(missions);
            }
            catch (InvalidDataException ex)
            {
                return new { Error = ex.Message };
            }

            // Resolve pipeline up front so the alias path expands stage chains the
            // same way the non-alias dispatch does. Single-stage Worker pipelines
            // (and null) keep the legacy single-mission-per-MD shape.
            Pipeline? pipeline = await admiral.ResolvePipelineAsync(pipelineId, vessel).ConfigureAwait(false);
            bool isMultiStage = pipeline != null
                && !(pipeline.Stages.Count == 1 && pipeline.Stages[0].PersonaName == "Worker");

            // Create the voyage record.
            Voyage voyage = new Voyage(title, description);
            voyage.TenantId = vessel.TenantId;
            voyage.UserId = vessel.UserId;
            voyage.Status = VoyageStatusEnum.Open;
            voyage = await database.Voyages.CreateAsync(voyage).ConfigureAwait(false);
            voyage.SelectedPlaybooks = ClonePlaybookSelectionsLocal(selectedPlaybooks);
            if (voyage.SelectedPlaybooks.Count > 0)
            {
                await database.Playbooks.SetVoyageSelectionsAsync(voyage.Id, voyage.SelectedPlaybooks).ConfigureAwait(false);
            }

            // Create missions in topological order, recording alias -> msn_* ID as each
            // mission (or stage chain) is persisted so that downstream missions can
            // resolve their deps. For multi-stage pipelines the alias points to the
            // LAST stage so dependents wait for the full review chain to finish.
            Dictionary<string, string> aliasToMsnId = new Dictionary<string, string>(StringComparer.Ordinal);
            bool anyAssigned = false;

            foreach (MissionDescription md in sortedMissions)
            {
                // External dep for the FIRST mission of this MD: alias-resolved (preferred)
                // or literal ID. For multi-stage chains, only the first stage carries this;
                // downstream stages depend on the previous stage in the chain.
                string? externalDep = null;
                if (!String.IsNullOrEmpty(md.DependsOnMissionAlias))
                    externalDep = aliasToMsnId[md.DependsOnMissionAlias];
                else if (!String.IsNullOrEmpty(md.DependsOnMissionId))
                    externalDep = md.DependsOnMissionId;

                // Per-mission playbooks: voyage-level defaults merged with the MD's own
                // selectedPlaybooks (caller deliveryMode wins on collision, new entries
                // append). Both legacy single-mission and multi-stage chain paths use
                // this merged list so individual missions in the same voyage can carry
                // different playbooks.
                List<SelectedPlaybook> mergedForMission = PlaybookMerge.MergeWithVesselDefaults(
                    voyage.SelectedPlaybooks,
                    md.SelectedPlaybooks ?? new List<SelectedPlaybook>());

                if (!isMultiStage)
                {
                    // Legacy single-mission-per-MD path.
                    Mission mission = new Mission(md.Title, md.Description);
                    mission.TenantId = vessel.TenantId;
                    mission.UserId = vessel.UserId;
                    mission.VoyageId = voyage.Id;
                    mission.VesselId = vesselId;
                    mission.PrestagedFiles = ClonePrestagedFilesLocal(md.PrestagedFiles);
                    mission.PreferredModel = md.PreferredModel;
                    mission.SelectedPlaybooks = ClonePlaybookSelectionsLocal(mergedForMission);
                    mission.DependsOnMissionId = externalDep;

                    mission = await admiral.DispatchMissionAsync(mission).ConfigureAwait(false);

                    if (mission.Status == MissionStatusEnum.Assigned || mission.Status == MissionStatusEnum.InProgress)
                        anyAssigned = true;

                    if (!String.IsNullOrEmpty(md.Alias))
                        aliasToMsnId[md.Alias] = mission.Id;
                    continue;
                }

                // Multi-stage pipeline: expand this MD into a chain of stage missions.
                // Stages with the same Order dispatch as parallel siblings that all depend on
                // the last mission of the previous order group rather than on each other.
                string baseTitle = md.Title.Length > 60 ? md.Title.Substring(0, 60).TrimEnd() + "..." : md.Title;
                string? previousOrderLastMissionId = null;
                string? lastStageMissionId = null;

                IOrderedEnumerable<IGrouping<int, PipelineStage>> stageGroups =
                    pipeline!.Stages.GroupBy(s => s.Order).OrderBy(g => g.Key);

                foreach (IGrouping<int, PipelineStage> stageGroup in stageGroups)
                {
                    // All stages in this group share the same upstream dependency.
                    string? groupDependencyId = previousOrderLastMissionId ?? externalDep;

                    string? lastMissionInGroup = null;

                    foreach (PipelineStage stage in stageGroup)
                    {
                        Mission stageMission = new Mission(
                            "[" + stage.PersonaName + "] " + baseTitle,
                            md.Description);
                        stageMission.TenantId = vessel.TenantId;
                        stageMission.UserId = vessel.UserId;
                        stageMission.VoyageId = voyage.Id;
                        stageMission.VesselId = vesselId;
                        stageMission.Persona = stage.PersonaName;
                        stageMission.DependsOnMissionId = groupDependencyId;
                        stageMission.PreferredModel = stage.PreferredModel ?? md.PreferredModel;
                        stageMission.SelectedPlaybooks = ClonePlaybookSelectionsLocal(mergedForMission);

                        // The very first mission of the chain gets the prestaged files.
                        bool isFirstChainMission = previousOrderLastMissionId == null && lastMissionInGroup == null;
                        if (isFirstChainMission)
                            stageMission.PrestagedFiles = ClonePrestagedFilesLocal(md.PrestagedFiles);

                        if (isFirstChainMission)
                        {
                            // First mission of the chain: dispatch through admiral so it gets
                            // the standard create + try-assign treatment (deps still gate
                            // assignment via MissionService.TryAssignAsync).
                            stageMission = await admiral.DispatchMissionAsync(stageMission).ConfigureAwait(false);
                            if (stageMission.Status == MissionStatusEnum.Assigned || stageMission.Status == MissionStatusEnum.InProgress)
                                anyAssigned = true;
                        }
                        else
                        {
                            // All other stages (downstream order or same-order sibling): persist as
                            // Pending. The captain pool picks them up after their dep completes.
                            stageMission = await database.Missions.CreateAsync(stageMission).ConfigureAwait(false);

                            // Persist playbook snapshots for non-first stages the same way
                            // admiral.DispatchMissionAsync does for the first stage. Without
                            // this, MissionService.GenerateClaudeMdAsync has no snapshots to
                            // render, resulting in a missing playbook section in the captain brief.
                            // A silent fallback LoggingModule is used when none was provided so
                            // snapshots are always persisted regardless of optional logging.
                            if (stageMission.SelectedPlaybooks != null
                                && stageMission.SelectedPlaybooks.Count > 0
                                && !String.IsNullOrEmpty(stageMission.TenantId))
                            {
                                LoggingModule effectiveLogging = logging ?? CreateSilentLogging();
                                IPlaybookService playbooks = new PlaybookService(database, effectiveLogging);
                                List<MissionPlaybookSnapshot> snapshots = await playbooks.CreateSnapshotsAsync(
                                    stageMission.TenantId,
                                    stageMission.SelectedPlaybooks).ConfigureAwait(false);
                                await database.Playbooks.SetMissionSnapshotsAsync(stageMission.Id, snapshots).ConfigureAwait(false);
                            }
                        }

                        lastMissionInGroup = stageMission.Id;
                        lastStageMissionId = stageMission.Id;
                    }

                    previousOrderLastMissionId = lastMissionInGroup;
                }

                // Map the user-visible alias to the LAST stage so any downstream alias
                // dep waits for the full review chain to complete, not just stage 1.
                if (!String.IsNullOrEmpty(md.Alias) && lastStageMissionId != null)
                    aliasToMsnId[md.Alias] = lastStageMissionId;
            }

            // Update voyage status to reflect whether any missions were immediately assigned.
            voyage.Status = anyAssigned ? VoyageStatusEnum.InProgress : VoyageStatusEnum.Open;
            voyage.LastUpdateUtc = DateTime.UtcNow;
            await database.Voyages.UpdateAsync(voyage).ConfigureAwait(false);

            return voyage;
        }

        /// <summary>
        /// Produces a shallow copy of a playbook selection list, avoiding
        /// shared-reference mutation between voyage and mission objects.
        /// </summary>
        private static List<SelectedPlaybook> ClonePlaybookSelectionsLocal(List<SelectedPlaybook>? selections)
        {
            if (selections == null || selections.Count == 0) return new List<SelectedPlaybook>();
            List<SelectedPlaybook> copy = new List<SelectedPlaybook>(selections.Count);
            foreach (SelectedPlaybook s in selections)
            {
                copy.Add(new SelectedPlaybook { PlaybookId = s.PlaybookId, DeliveryMode = s.DeliveryMode });
            }
            return copy;
        }

        /// <summary>
        /// Produces a shallow copy of a prestaged-file list.
        /// </summary>
        private static List<PrestagedFile>? ClonePrestagedFilesLocal(List<PrestagedFile>? entries)
        {
            if (entries == null || entries.Count == 0) return null;
            List<PrestagedFile> copy = new List<PrestagedFile>(entries.Count);
            foreach (PrestagedFile entry in entries)
            {
                if (entry == null) continue;
                copy.Add(new PrestagedFile(entry.SourcePath ?? "", entry.DestPath ?? ""));
            }
            return copy.Count > 0 ? copy : null;
        }

        /// <summary>
        /// Merges vessel default playbooks with caller-supplied playbooks.
        /// The merged list starts with vessel defaults. For each caller entry:
        /// if the playbookId already appears in the defaults, the default entry's
        /// deliveryMode is replaced with the caller's value; otherwise the caller
        /// entry is appended. This ensures vessel defaults are always present while
        /// callers can override delivery modes or add additional playbooks.
        /// </summary>
        /// <param name="defaults">Vessel default playbooks (may be null or empty).</param>
        /// <param name="callerEntries">Caller-supplied playbooks (may be empty).</param>
        /// <returns>Merged playbook list.</returns>
        private static List<SelectedPlaybook> MergePlaybooks(List<SelectedPlaybook>? defaults, List<SelectedPlaybook> callerEntries)
        {
            return PlaybookMerge.MergeWithVesselDefaults(defaults, callerEntries);
        }

        /// <summary>
        /// Creates a <see cref="LoggingModule"/> with console output disabled, used as a
        /// fallback when no logging module is supplied to <see cref="Register"/> so that
        /// downstream stage snapshot persistence is never silently skipped.
        /// </summary>
        private static LoggingModule CreateSilentLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }
    }
}
