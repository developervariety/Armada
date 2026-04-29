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
        /// <remarks>
        /// armada_dispatch accepts an optional <c>prestagedFiles</c> array on each
        /// mission entry. Each entry copies an absolute <c>sourcePath</c> on the
        /// Admiral host into a relative <c>destPath</c> within the dock worktree
        /// before the captain process is launched. Useful for handing the captain
        /// a context snapshot, generated test fixture, or one-shot input file
        /// that should not be committed to the vessel repository.
        /// </remarks>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, IAdmiralService admiral, ArmadaSettings? settings = null)
        {
            register(
                "armada_dispatch",
                "Dispatch a new voyage with missions to a vessel. Each mission may include an optional prestagedFiles array of {sourcePath, destPath} entries; the Admiral copies sourcePath (absolute, on the Admiral host) into destPath (relative, inside the dock worktree) after the dock is created and before the captain spawns. Each mission may also pin a specific captain via preferredCaptainId or restrict assignment to captains whose Model matches preferredModel (case-insensitive).",
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
                                    preferredCaptainId = new { type = "string", description = "Optional captain ID (cap_ prefix) to pin this mission to. If the pinned captain is busy at dispatch time, the mission stays Pending until the next dispatch tick when that captain is idle." },
                                    preferredModel = new { type = "string", description = "Optional captain Model filter (case-insensitive). Only idle captains whose Model matches will be considered for this mission; persona-preference logic runs within that filtered set." },
                                    dependsOnMissionId = new { type = "string", description = "Optional mission ID (msn_ prefix) this mission must wait for. The dependent mission stays Pending until the referenced mission reaches a completion state." },
                                    alias = new { type = "string", description = "Optional logical alias for this mission within this dispatch batch (e.g. 'M1', 'resolver'). Other missions in the same batch may reference it via dependsOnMissionAlias. Must be unique within the batch." },
                                    dependsOnMissionAlias = new { type = "string", description = "Optional alias of another mission in this same dispatch batch that this mission must wait for. The server resolves the alias to the concrete msn_* ID after creating the dependency mission. Takes precedence over dependsOnMissionId when both are supplied." }
                                }
                            }
                        },
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

                    // When any mission carries an alias or dependsOnMissionAlias, use the
                    // alias-aware dispatch path which topologically sorts missions and
                    // resolves alias references to concrete msn_* IDs at creation time.
                    bool hasAliases = missions.Any(m =>
                        !String.IsNullOrEmpty(m.Alias) || !String.IsNullOrEmpty(m.DependsOnMissionAlias));
                    if (hasAliases)
                    {
                        return await DispatchWithAliasesAsync(
                            database, admiral, title, description, vesselId,
                            dispatchVessel, missions, mergedPlaybooks).ConfigureAwait(false);
                    }

                    // Use pipeline-aware dispatch if pipelineId is provided
                    string? pipelineId = request.PipelineId;
                    if (String.IsNullOrEmpty(pipelineId) && !String.IsNullOrEmpty(request.Pipeline))
                    {
                        // Resolve pipeline name to ID
                        Pipeline? namedPipeline = await database.Pipelines.ReadByNameAsync(request.Pipeline).ConfigureAwait(false);
                        if (namedPipeline != null) pipelineId = namedPipeline.Id;
                        else return (object)new { Error = "Pipeline not found: " + request.Pipeline };
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

                    List<Mission> missions = await database.Missions.EnumerateByVoyageAsync(voyageId).ConfigureAwait(false);

                    // Default: summary mode (returns voyage metadata + mission counts by status, no mission objects)
                    bool isSummary = request.Summary != false;
                    if (isSummary)
                    {
                        Dictionary<string, int> counts = missions.GroupBy(m => m.Status.ToString())
                            .ToDictionary(g => g.Key, g => g.Count());
                        return (object)new
                        {
                            Voyage = new { voyage.Id, voyage.Title, voyage.Description, voyage.Status, voyage.CreatedUtc, voyage.LastUpdateUtc },
                            TotalMissions = missions.Count,
                            MissionCountsByStatus = counts
                        };
                    }

                    // Non-summary: optionally include mission objects
                    if (request.IncludeMissions != true)
                    {
                        return (object)new { Voyage = voyage, TotalMissions = missions.Count };
                    }

                    // Full mission objects with optional field inclusion
                    foreach (Mission m in missions)
                    {
                        m.DiffSnapshot = request.IncludeDiffs == true ? m.DiffSnapshot : null;
                        if (request.IncludeDescription != true) m.Description = null;
                        // includeLogs is reserved for future use -- logs are stored in external files
                        // and are not available on the mission object. This flag currently has no effect.
                    }
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

                    // Cancel only pending/assigned missions (in-progress work is left running)
                    List<Mission> missions = await database.Missions.EnumerateByVoyageAsync(voyageId).ConfigureAwait(false);
                    int cancelledCount = 0;
                    foreach (Mission m in missions)
                    {
                        if (m.Status == MissionStatusEnum.Pending || m.Status == MissionStatusEnum.Assigned)
                        {
                            // Release the captain if this mission was assigned to one
                            if (!String.IsNullOrEmpty(m.CaptainId))
                            {
                                Captain? captain = await database.Captains.ReadAsync(m.CaptainId).ConfigureAwait(false);
                                if (captain != null && captain.CurrentMissionId == m.Id)
                                {
                                    List<Mission> otherMissions = (await database.Missions.EnumerateByCaptainAsync(captain.Id).ConfigureAwait(false))
                                        .Where(om => om.Id != m.Id && (om.Status == MissionStatusEnum.InProgress || om.Status == MissionStatusEnum.Assigned)).ToList();
                                    if (otherMissions.Count == 0)
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

                            m.Status = MissionStatusEnum.Cancelled;
                            m.CompletedUtc = DateTime.UtcNow;
                            m.LastUpdateUtc = DateTime.UtcNow;
                            await database.Missions.UpdateAsync(m).ConfigureAwait(false);
                            cancelledCount++;
                        }
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

        /// <summary>
        /// Dispatch handler for missions that use logical alias-based
        /// inter-mission dependencies. Validates and topologically sorts the
        /// alias graph, creates a single voyage, then creates each mission in
        /// dependency order -- resolving alias references to concrete msn_* IDs
        /// as each mission is persisted.
        /// </summary>
        private static async Task<object> DispatchWithAliasesAsync(
            DatabaseDriver database,
            IAdmiralService admiral,
            string title,
            string description,
            string vesselId,
            Vessel? vessel,
            List<MissionDescription> missions,
            List<SelectedPlaybook> selectedPlaybooks)
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
            // mission is persisted so that downstream missions can resolve their deps.
            Dictionary<string, string> aliasToMsnId = new Dictionary<string, string>(StringComparer.Ordinal);
            bool anyAssigned = false;

            foreach (MissionDescription md in sortedMissions)
            {
                Mission mission = new Mission(md.Title, md.Description);
                mission.TenantId = vessel.TenantId;
                mission.UserId = vessel.UserId;
                mission.VoyageId = voyage.Id;
                mission.VesselId = vesselId;
                mission.PrestagedFiles = ClonePrestagedFilesLocal(md.PrestagedFiles);
                mission.PreferredCaptainId = md.PreferredCaptainId;
                mission.PreferredModel = md.PreferredModel;
                mission.SelectedPlaybooks = ClonePlaybookSelectionsLocal(voyage.SelectedPlaybooks);

                // Alias-based dependency takes precedence over a literal ID.
                if (!String.IsNullOrEmpty(md.DependsOnMissionAlias))
                    mission.DependsOnMissionId = aliasToMsnId[md.DependsOnMissionAlias];
                else if (!String.IsNullOrEmpty(md.DependsOnMissionId))
                    mission.DependsOnMissionId = md.DependsOnMissionId;

                mission = await admiral.DispatchMissionAsync(mission).ConfigureAwait(false);

                if (mission.Status == MissionStatusEnum.Assigned || mission.Status == MissionStatusEnum.InProgress)
                    anyAssigned = true;

                // Record alias so later missions can reference this mission's msn_* ID.
                if (!String.IsNullOrEmpty(md.Alias))
                    aliasToMsnId[md.Alias] = mission.Id;
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
    }
}
