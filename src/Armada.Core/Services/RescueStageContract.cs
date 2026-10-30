namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Authorization;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>Preserves the original voyage's required stages through recovery.</summary>
    public static class RescueStageContract
    {
        /// <summary>Reason prefix for a rescue that omits required stages.</summary>
        public const string MissingStageCode = "rescue_required_stage_missing";

        /// <summary>
        /// Refuse a rescue whose mission graph omits an original required stage. The original
        /// recorded pipeline wins over rescue records and subsequent pipeline edits. Explicit
        /// recorded stage skips keep their existing authority; a shortened rescue is not a skip.
        /// </summary>
        /// <param name="database">Database.</param>
        /// <param name="voyageId">Voyage being landed or reconciled.</param>
        /// <param name="token">Cancellation.</param>
        /// <returns>The refusal, or null when no required stage is missing.</returns>
        public static async Task<string?> DescribeRefusalAsync(DatabaseDriver database, string? voyageId,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrWhiteSpace(voyageId)) return null;
            List<Mission> candidate = await database.Missions.EnumerateByVoyageAsync(voyageId, token).ConfigureAwait(false);
            Mission? root = candidate.FirstOrDefault(item => RescueMissionMarker.IsAutoRescue(item)
                && !String.IsNullOrWhiteSpace(item.ParentMissionId));
            if (root == null) return null;

            List<Mission> original = candidate;
            string originalVoyageId = voyageId;
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal) { voyageId };
            while (root != null)
            {
                Mission? parent = await database.Missions.ReadAsync(root.ParentMissionId!, token).ConfigureAwait(false);
                if (parent == null || !OwnershipPolicy.SameTenant(parent.TenantId, root.TenantId)
                    || OwnershipPolicy.UserOfRecord(parent.UserId) != OwnershipPolicy.UserOfRecord(root.UserId)
                    || parent.VesselId != root.VesselId)
                    return "rescue_required_stage_contract_unavailable: recovery parent is unavailable";
                // A standalone mission never had a voyage stage contract.
                if (String.IsNullOrWhiteSpace(parent.VoyageId)) return null;
                if (!visited.Add(parent.VoyageId))
                    return "rescue_required_stage_contract_unavailable: recovery ancestry contains a cycle";
                originalVoyageId = parent.VoyageId;
                original = await database.Missions.EnumerateByVoyageAsync(originalVoyageId, token).ConfigureAwait(false);
                root = original.FirstOrDefault(item => RescueMissionMarker.IsAutoRescue(item)
                    && !String.IsNullOrWhiteSpace(item.ParentMissionId));
            }

            Pipeline? recorded = await VoyagePipelineRecord.ReadAsync(database, originalVoyageId, null, token).ConfigureAwait(false);
            List<PipelineStage> required = recorded?.Stages ?? original.Select((item, index) =>
                new PipelineStage(item.StageOrder ?? index + 1, item.Persona ?? "Worker")).ToList();
            if (required.Count == 0)
                return "rescue_required_stage_contract_unavailable: original voyage has no stages";

            List<ArmadaEvent> events = await database.Events.EnumerateByEntityAsync(
                "voyage", originalVoyageId, VoyagePipelineRecord.EventSearchLimit, token).ConfigureAwait(false);
            foreach (ArmadaEvent entry in events.Where(item => item.EventType == PipelineStageSkip.StageSkippedEventType))
            {
                StageSkipEvidence? skip;
                try { skip = JsonSerializer.Deserialize<StageSkipEvidence>(entry.Payload ?? ""); }
                catch (JsonException) { continue; }
                if (skip == null || String.IsNullOrWhiteSpace(skip.Reason) || String.IsNullOrWhiteSpace(skip.Confirmer)) continue;
                required = required.Where(stage => stage.Order != skip.StageOrder
                    || !PersonaCatalog.Matches(stage.PersonaName, skip.Persona)).ToList();
            }

            List<string> missing = new List<string>();
            if (recorded != null && candidate.All(item => item.StageOrder.HasValue))
            {
                foreach (PipelineStage stage in required)
                    if (!candidate.Any(item => item.StageOrder == stage.Order && PersonaCatalog.Matches(item.Persona, stage.PersonaName)))
                        missing.Add(stage.PersonaName + "(" + stage.Order + ")");
            }
            else
            {
                // Legacy mission rows may not carry orders. Preserve repeated roles by count,
                // rather than accepting one Worker as proof for both Worker stages.
                foreach (IGrouping<string, PipelineStage> group in required.GroupBy(item => item.PersonaName, StringComparer.OrdinalIgnoreCase))
                    if (candidate.Count(item => PersonaCatalog.Matches(item.Persona, group.Key)) < group.Count())
                        missing.Add(group.Key + " x" + group.Count());
            }
            return missing.Count == 0 ? null : MissingStageCode + ": " + String.Join(", ", missing);
        }

        private sealed class StageSkipEvidence
        {
            public string Persona { get; set; } = "";
            public int StageOrder { get; set; }
            public string Reason { get; set; } = "";
            public string Confirmer { get; set; } = "";
        }
    }
}
