namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// MCP tools exposing the auto-land safety-net audit queue and verdict recording.
    /// armada_drain_audit_queue: orchestrator pulls Pending entries flagged for deep review.
    /// armada_record_audit_verdict: orchestrator records the subagent's Pass/Concern/Critical verdict.
    /// </summary>
    public static class McpAuditTools
    {
        /// <summary>
        /// Registers audit MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for data access.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database)
        {
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
                    if (args.HasValue && args.Value.TryGetProperty("vesselId", out JsonElement v) && v.ValueKind == JsonValueKind.String)
                        vesselId = v.GetString();
                    if (args.HasValue && args.Value.TryGetProperty("limit", out JsonElement l) && l.ValueKind == JsonValueKind.Number)
                        limit = Math.Clamp(l.GetInt32(), 1, 50);

                    List<MergeEntry> all = await database.MergeEntries.EnumerateAsync().ConfigureAwait(false);
                    IEnumerable<MergeEntry> pending = all
                        .Where(e => e.AuditDeepPicked == true
                                 && e.AuditDeepVerdict == "Pending"
                                 && e.AuditDeepCompletedUtc == null
                                 && (vesselId == null || e.VesselId == vesselId))
                        .OrderBy(e => e.CreatedUtc)
                        .Take(limit);

                    List<object> results = new List<object>();
                    foreach (MergeEntry entry in pending)
                    {
                        Vessel? vessel = await database.Vessels.ReadAsync(entry.VesselId!).ConfigureAwait(false);
                        bool isCalibration = (vessel?.AutoLandCalibrationLandedCount ?? 0) < 50;
                        results.Add(new
                        {
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
                    return (object)results;
                });

            register(
                "armada_record_audit_verdict",
                "Records the orchestrator-provided audit verdict (Pass/Concern/Critical) on a deep-review merge entry",
                new
                {
                    type = "object",
                    properties = new
                    {
                        entryId = new { type = "string", description = "Merge entry ID (mrg_ prefix)" },
                        verdict = new { type = "string", description = "Pass | Concern | Critical" },
                        notes = new { type = "string", description = "Subagent audit notes" },
                        recommendedAction = new { type = "string", description = "Required when verdict = Critical; null otherwise" }
                    },
                    required = new[] { "entryId", "verdict", "notes" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };

                    string entryId = args.Value.GetProperty("entryId").GetString()!;
                    string verdict = args.Value.GetProperty("verdict").GetString()!;
                    string notes = args.Value.GetProperty("notes").GetString()!;
                    string? recAction = null;
                    if (args.Value.TryGetProperty("recommendedAction", out JsonElement ra) && ra.ValueKind == JsonValueKind.String)
                        recAction = ra.GetString();

                    if (verdict != "Pass" && verdict != "Concern" && verdict != "Critical")
                        return (object)new { Error = "verdict must be Pass | Concern | Critical" };
                    if (verdict == "Critical" && string.IsNullOrEmpty(recAction))
                        return (object)new { Error = "recommendedAction required when verdict = Critical" };

                    MergeEntry? entry = await database.MergeEntries.ReadAsync(entryId).ConfigureAwait(false);
                    if (entry == null) return (object)new { Error = "merge entry not found: " + entryId };

                    entry.AuditDeepVerdict = verdict;
                    entry.AuditDeepNotes = notes;
                    entry.AuditDeepRecommendedAction = recAction;
                    entry.AuditDeepCompletedUtc = DateTime.UtcNow;
                    entry.LastUpdateUtc = DateTime.UtcNow;
                    entry = await database.MergeEntries.UpdateAsync(entry).ConfigureAwait(false);
                    return (object)entry;
                });
        }
    }
}
