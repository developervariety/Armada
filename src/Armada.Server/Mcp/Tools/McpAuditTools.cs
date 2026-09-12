namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Memory;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// MCP tools exposing the auto-land safety-net audit queue and verdict recording.
    /// armada_drain_audit_queue: orchestrator pulls Pending entries flagged for deep review.
    /// armada_record_audit_verdict: orchestrator records the subagent's Pass/Concern/Critical verdict.
    /// </summary>
    public static class McpAuditTools
    {
        private class AuditQueueItem
        {
            public DateTime CreatedUtc { get; set; }
            public MergeEntry? MergeEntry { get; set; }
            public JudgeFollowUp? JudgeFollowUp { get; set; }
        }

        /// <summary>
        /// Registers audit MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for data access.</param>
        /// <param name="remoteTriggerService">Optional remote trigger service; when provided, fires FireCriticalAsync on Critical verdicts.</param>
        /// <param name="reflectionDispatcher">Optional reflection dispatcher; when null, audit drain does not auto-dispatch reflections.</param>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            IRemoteTriggerService? remoteTriggerService = null,
            ReflectionDispatcher? reflectionDispatcher = null)
        {
            JudgeFollowUpService followUpService = new JudgeFollowUpService(database, new SyslogLogging.LoggingModule());
            JudgeFollowUpBackfillService backfillService = new JudgeFollowUpBackfillService(database, new SyslogLogging.LoggingModule());
            register(
                "armada_backfill_judge_followups",
                "Backfills missed durable Judge follow-up captures over a bounded UTC time range; safe to repeat",
                new
                {
                    type = "object",
                    properties = new
                    {
                        fromUtc = new { type = "string", description = "Exclusive UTC range start in ISO-8601 format" },
                        toUtc = new { type = "string", description = "Exclusive UTC range end in ISO-8601 format; defaults to now" },
                        dryRun = new { type = "boolean", description = "Report missing rows without writing them" },
                        maxPages = new { type = "integer", description = "Maximum 500-row pages to scan (default 100, max 1000)" }
                    },
                    required = new[] { "fromUtc" }
                },
                async (args) =>
                {
                    if (!args.HasValue
                        || !args.Value.TryGetProperty("fromUtc", out JsonElement fromElement)
                        || fromElement.ValueKind != JsonValueKind.String
                        || !DateTime.TryParse(fromElement.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime fromUtc))
                        return (object)new { Error = "fromUtc must be an ISO-8601 timestamp" };

                    DateTime toUtc = DateTime.UtcNow;
                    if (args.Value.TryGetProperty("toUtc", out JsonElement toElement)
                        && (toElement.ValueKind != JsonValueKind.String
                            || !DateTime.TryParse(toElement.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out toUtc)))
                        return (object)new { Error = "toUtc must be an ISO-8601 timestamp" };
                    bool dryRun = args.Value.TryGetProperty("dryRun", out JsonElement dryRunElement)
                        && dryRunElement.ValueKind == JsonValueKind.True;
                    int maxPages = 100;
                    if (args.Value.TryGetProperty("maxPages", out JsonElement maxPagesElement)
                        && maxPagesElement.ValueKind == JsonValueKind.Number)
                        maxPages = Math.Clamp(maxPagesElement.GetInt32(), 1, 1000);
                    if (toUtc <= fromUtc) return (object)new { Error = "toUtc must be later than fromUtc" };

                    return await backfillService.RunAsync(
                        fromUtc.ToUniversalTime(),
                        toUtc.ToUniversalTime(),
                        dryRun,
                        maxPages).ConfigureAwait(false);
                });
            register(
                "armada_drain_audit_queue",
                "Returns Pending deep-review merge entries oldest-first for orchestrator audit processing",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Optional: filter to one vessel" },
                        limit = new { type = "integer", description = "Max entries to return (default 10, max 50)" }
                    }
                },
                async (args) =>
                {
                    string? vesselId = null;
                    int limit = 10;
                    if (args.HasValue && args.Value.TryGetProperty("vesselId", out JsonElement vidEl) && vidEl.ValueKind == JsonValueKind.String)
                        vesselId = vidEl.GetString();
                    if (args.HasValue && args.Value.TryGetProperty("limit", out JsonElement l) && l.ValueKind == JsonValueKind.Number)
                        limit = Math.Clamp(l.GetInt32(), 1, 50);

                    await followUpService.ReconcilePendingAssociationsAsync(vesselId).ConfigureAwait(false);

                    List<MergeEntry> all = await database.MergeEntries.EnumerateAsync().ConfigureAwait(false);
                    List<JudgeFollowUp> pendingFollowUps = await database.JudgeFollowUps
                        .EnumeratePendingAsync(vesselId)
                        .ConfigureAwait(false);
                    HashSet<string> linkedEntryIds = pendingFollowUps
                        .Where(followUp => !String.IsNullOrWhiteSpace(followUp.MergeEntryId))
                        .Select(followUp => followUp.MergeEntryId!)
                        .ToHashSet(StringComparer.Ordinal);
                    IEnumerable<MergeEntry> pendingEntries = all
                        .Where(e => e.AuditDeepPicked == true
                                 && e.AuditDeepVerdict == "Pending"
                                 && e.AuditDeepCompletedUtc == null
                                 && (vesselId == null || e.VesselId == vesselId));

                    List<AuditQueueItem> pending = new List<AuditQueueItem>();
                    foreach (MergeEntry entry in pendingEntries)
                    {
                        if (linkedEntryIds.Contains(entry.Id)) continue;
                        JudgeFollowUp? canonical = await database.JudgeFollowUps
                            .ReadByMergeEntryAsync(entry.Id)
                            .ConfigureAwait(false);
                        if (canonical != null) continue;
                        pending.Add(new AuditQueueItem
                        {
                            CreatedUtc = entry.CreatedUtc,
                            MergeEntry = entry
                        });
                    }
                    pending.AddRange(pendingFollowUps.Select(followUp => new AuditQueueItem
                    {
                        CreatedUtc = followUp.CreatedUtc,
                        JudgeFollowUp = followUp
                    }));

                    List<object> results = new List<object>();
                    foreach (AuditQueueItem item in pending
                        .OrderBy(candidate => candidate.CreatedUtc)
                        .ThenBy(candidate => candidate.MergeEntry?.Id ?? candidate.JudgeFollowUp!.Id, StringComparer.Ordinal)
                        .Take(limit))
                    {
                        if (item.JudgeFollowUp != null)
                        {
                            JudgeFollowUp followUp = item.JudgeFollowUp;
                            MergeEntry? linkedEntry = String.IsNullOrWhiteSpace(followUp.MergeEntryId)
                                ? null
                                : all.FirstOrDefault(entry => String.Equals(entry.Id, followUp.MergeEntryId, StringComparison.Ordinal));
                            Vessel? followUpVessel = String.IsNullOrWhiteSpace(followUp.VesselId)
                                ? null
                                : await database.Vessels.ReadAsync(followUp.VesselId).ConfigureAwait(false);
                            bool followUpCalibration = (followUpVessel?.AutoLandCalibrationLandedCount ?? 0) < 50;
                            results.Add(new
                            {
                                kind = "judgeFollowUp",
                                auditItemId = followUp.Id,
                                followUpId = followUp.Id,
                                judgeMissionId = followUp.JudgeMissionId,
                                missionId = followUp.ReviewedMissionId,
                                entryId = followUp.MergeEntryId,
                                vesselId = followUp.VesselId,
                                judgeVerdict = followUp.JudgeVerdict,
                                suggestedFollowUps = followUp.SuggestedFollowUps,
                                branchName = linkedEntry?.BranchName,
                                auditLane = linkedEntry?.AuditLane,
                                auditCriticalTrigger = linkedEntry?.AuditCriticalTrigger,
                                auditConventionNotes = linkedEntry?.AuditConventionNotes,
                                isCalibration = followUpCalibration
                            });
                            continue;
                        }

                        MergeEntry entry = item.MergeEntry!;
                        Vessel? vessel = await database.Vessels.ReadAsync(entry.VesselId!).ConfigureAwait(false);
                        bool isCalibration = (vessel?.AutoLandCalibrationLandedCount ?? 0) < 50;
                        results.Add(new
                        {
                            kind = "mergeEntry",
                            auditItemId = entry.Id,
                            entryId = entry.Id,
                            missionId = entry.MissionId,
                            vesselId = entry.VesselId,
                            branchName = entry.BranchName,
                            auditLane = entry.AuditLane,
                            auditCriticalTrigger = entry.AuditCriticalTrigger,
                            auditConventionNotes = entry.AuditConventionNotes,
                            isCalibration
                        });
                    }

                    List<object> reflectionsDispatched = new List<object>();
                    if (reflectionDispatcher != null)
                    {
                        List<Vessel> vesselsToCheck;
                        if (!String.IsNullOrEmpty(vesselId))
                        {
                            Vessel? single = await database.Vessels.ReadAsync(vesselId).ConfigureAwait(false);
                            vesselsToCheck = single != null ? new List<Vessel> { single } : new List<Vessel>();
                        }
                        else
                        {
                            List<Vessel> allVessels = await database.Vessels.EnumerateAsync().ConfigureAwait(false);
                            vesselsToCheck = new List<Vessel>();
                            foreach (Vessel v in allVessels)
                            {
                                if (v.Active)
                                    vesselsToCheck.Add(v);
                            }
                        }

                        foreach (Vessel checkVessel in vesselsToCheck)
                        {
                            ReflectionDispatcher.DispatchResult? consolidateDispatched = await reflectionDispatcher
                                .TryAutoDispatchAfterAuditDrainAsync(checkVessel)
                                .ConfigureAwait(false);
                            if (consolidateDispatched != null)
                            {
                                reflectionsDispatched.Add(new
                                {
                                    vesselId = checkVessel.Id,
                                    missionId = consolidateDispatched.MissionId,
                                    mode = "consolidate"
                                });
                                continue;
                            }

                            ReflectionDispatcher.DispatchResult? reorganizeDispatched = await reflectionDispatcher
                                .TryAutoDispatchReorganizeAfterAuditDrainAsync(checkVessel)
                                .ConfigureAwait(false);
                            if (reorganizeDispatched != null)
                            {
                                reflectionsDispatched.Add(new
                                {
                                    vesselId = checkVessel.Id,
                                    missionId = reorganizeDispatched.MissionId,
                                    mode = "reorganize"
                                });
                                continue;
                            }

                            ReflectionDispatcher.DispatchResult? packCurateDispatched = await reflectionDispatcher
                                .TryAutoDispatchPackCurateAfterAuditDrainAsync(checkVessel)
                                .ConfigureAwait(false);
                            if (packCurateDispatched != null)
                            {
                                reflectionsDispatched.Add(new
                                {
                                    vesselId = checkVessel.Id,
                                    missionId = packCurateDispatched.MissionId,
                                    mode = "pack-curate"
                                });
                            }
                        }

                        // v2-F2: identity-scope auto-triggers (persona-curate / captain-curate).
                        // Pick the first active vessel as the worktree anchor for cross-vessel
                        // identity dispatches; the brief itself is vessel-agnostic.
                        Vessel? identityAnchor = vesselsToCheck.Count > 0 ? vesselsToCheck[0] : null;
                        if (identityAnchor != null)
                        {
                            List<Persona> personas = await database.Personas.EnumerateAsync().ConfigureAwait(false);
                            foreach (Persona p in personas)
                            {
                                if (!p.Active) continue;
                                if (String.Equals(p.Name, "MemoryConsolidator", StringComparison.OrdinalIgnoreCase)) continue;
                                ReflectionDispatcher.DispatchResult? personaDispatched = await reflectionDispatcher
                                    .TryAutoDispatchPersonaCurateAfterAuditDrainAsync(p, identityAnchor)
                                    .ConfigureAwait(false);
                                if (personaDispatched != null)
                                {
                                    reflectionsDispatched.Add(new
                                    {
                                        personaName = p.Name,
                                        missionId = personaDispatched.MissionId,
                                        mode = "persona-curate"
                                    });
                                }
                            }

                            List<Captain> captains = await database.Captains.EnumerateAsync().ConfigureAwait(false);
                            foreach (Captain c in captains)
                            {
                                if (!c.CurateThreshold.HasValue) continue;
                                ReflectionDispatcher.DispatchResult? captainDispatched = await reflectionDispatcher
                                    .TryAutoDispatchCaptainCurateAfterAuditDrainAsync(c, identityAnchor)
                                    .ConfigureAwait(false);
                                if (captainDispatched != null)
                                {
                                    reflectionsDispatched.Add(new
                                    {
                                        captainId = c.Id,
                                        missionId = captainDispatched.MissionId,
                                        mode = "captain-curate"
                                    });
                                }
                            }

                            // v2-F3: fleet-scope auto-trigger. Iterate active fleets; require
                            // CurateThreshold be set per fleet (NULL disables the trigger).
                            // Anchor-vessel resolution: prefer an active vessel from the same
                            // fleet so worktree provisioning lands in a fleet member.
                            List<Fleet> fleets = await database.Fleets.EnumerateAsync().ConfigureAwait(false);
                            foreach (Fleet f in fleets)
                            {
                                if (!f.Active) continue;
                                if (!f.CurateThreshold.HasValue) continue;
                                List<Vessel> fleetVessels = await database.Vessels.EnumerateByFleetAsync(f.Id).ConfigureAwait(false);
                                Vessel? fleetAnchor = null;
                                foreach (Vessel fv in fleetVessels)
                                {
                                    if (fv.Active) { fleetAnchor = fv; break; }
                                }
                                fleetAnchor ??= identityAnchor;
                                if (fleetAnchor == null) continue;

                                ReflectionDispatcher.DispatchResult? fleetDispatched = await reflectionDispatcher
                                    .TryAutoDispatchFleetCurateAfterAuditDrainAsync(f, fleetAnchor)
                                    .ConfigureAwait(false);
                                if (fleetDispatched != null)
                                {
                                    reflectionsDispatched.Add(new
                                    {
                                        fleetId = f.Id,
                                        missionId = fleetDispatched.MissionId,
                                        mode = "fleet-curate"
                                    });
                                }
                            }
                        }
                    }

                    return (object)new { entries = results, reflectionsDispatched };
                });

            register(
                "armada_record_audit_verdict",
                "Records the orchestrator-provided audit verdict (Pass/Concern/Critical) on a deep-review merge entry",
                new
                {
                    type = "object",
                    properties = new
                    {
                        entryId = new { type = "string", description = "Merge entry ID (mrg_ prefix); mutually exclusive with followUpId" },
                        followUpId = new { type = "string", description = "Judge follow-up ID (jfu_ prefix); mutually exclusive with entryId" },
                        verdict = new { type = "string", description = "Pass | Concern | Critical" },
                        notes = new { type = "string", description = "Subagent audit notes" },
                        recommendedAction = new { type = "string", description = "Required when verdict = Critical; null otherwise" }
                    },
                    required = new[] { "verdict", "notes" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };

                    string? entryId = args.Value.TryGetProperty("entryId", out JsonElement entryIdElement)
                        && entryIdElement.ValueKind == JsonValueKind.String
                        ? entryIdElement.GetString()
                        : null;
                    string? followUpId = args.Value.TryGetProperty("followUpId", out JsonElement followUpIdElement)
                        && followUpIdElement.ValueKind == JsonValueKind.String
                        ? followUpIdElement.GetString()
                        : null;
                    string verdict = args.Value.GetProperty("verdict").GetString()!;
                    string notes = args.Value.GetProperty("notes").GetString()!;
                    string? recAction = null;
                    if (args.Value.TryGetProperty("recommendedAction", out JsonElement ra) && ra.ValueKind == JsonValueKind.String)
                        recAction = ra.GetString();

                    if (verdict != "Pass" && verdict != "Concern" && verdict != "Critical")
                        return (object)new { Error = "verdict must be Pass | Concern | Critical" };
                    if (verdict == "Critical" && string.IsNullOrEmpty(recAction))
                        return (object)new { Error = "recommendedAction required when verdict = Critical" };
                    if (String.IsNullOrWhiteSpace(entryId) == String.IsNullOrWhiteSpace(followUpId))
                        return (object)new { Error = "provide exactly one of entryId or followUpId" };

                    if (String.IsNullOrWhiteSpace(followUpId) && !String.IsNullOrWhiteSpace(entryId))
                    {
                        List<JudgeFollowUp> linkedPending = await database.JudgeFollowUps
                            .EnumeratePendingAsync()
                            .ConfigureAwait(false);
                        followUpId = linkedPending
                            .FirstOrDefault(item => String.Equals(item.MergeEntryId, entryId, StringComparison.Ordinal))
                            ?.Id;
                    }

                    if (!String.IsNullOrWhiteSpace(followUpId))
                    {
                        JudgeFollowUp? followUp = await database.JudgeFollowUps.ReadAsync(followUpId).ConfigureAwait(false);
                        if (followUp == null) return (object)new { Error = "judge follow-up not found: " + followUpId };

                        DateTime completedUtc = DateTime.UtcNow;
                        followUp = await followUpService.CompleteAuditAsync(
                            followUp.Id,
                            verdict,
                            notes,
                            recAction,
                            completedUtc).ConfigureAwait(false);

                        if (verdict == "Critical" && remoteTriggerService != null)
                        {
                            string followUpContext = "audit Critical on Judge follow-up " + followUp.Id + " :: "
                                + notes + " :: ACTION: " + (recAction ?? "(none)");
                            try
                            {
                                await remoteTriggerService.FireCriticalAsync(followUpContext).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                // Fire failure does not affect verdict recording.
                            }
                        }

                        return (object)followUp;
                    }

                    MergeEntry? entry = await database.MergeEntries.ReadAsync(entryId!).ConfigureAwait(false);
                    if (entry == null) return (object)new { Error = "merge entry not found: " + entryId };

                    entry.AuditDeepVerdict = verdict;
                    entry.AuditDeepNotes = notes;
                    entry.AuditDeepRecommendedAction = recAction;
                    entry.AuditDeepCompletedUtc = DateTime.UtcNow;
                    entry.LastUpdateUtc = DateTime.UtcNow;
                    entry = await database.MergeEntries.UpdateAsync(entry).ConfigureAwait(false);

                    if (verdict == "Critical" && remoteTriggerService != null)
                    {
                        string ctx = "audit Critical on entry " + entry.Id + " :: " + (notes ?? "") + " :: ACTION: " + (recAction ?? "(none)");
                        try
                        {
                            await remoteTriggerService.FireCriticalAsync(ctx).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // fire failure does not affect verdict recording
                        }
                    }

                    return (object)entry;
                });
        }
    }
}
