namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    public partial class MissionService
    {
        /// <summary>
        /// The task-local acceptance criteria a Judge PASS is measured against when the Judge reviews an
        /// intermediate Architect slice, or null when the Judge owns the whole objective. A Judge owns the
        /// whole objective unless every one of these holds: its chain starts at a Worker the Architect fan-out
        /// created, that slice is not the integration mission, a later slice of the same plan follows it
        /// (it is an ancestor of another slice's first stage, or a member of the plan's integration barrier),
        /// and the slice root's description declares at least one criterion in a form the Architect plan defines. Any missing signal returns null, so the
        /// caller measures the linked objective's live criteria and an undeterminable scope never relaxes the walk.
        /// </summary>
        /// <param name="judge">The Judge mission.</param>
        /// <param name="voyageMissions">Every mission of the Judge's voyage.</param>
        /// <returns>The slice-local criteria, or null when the Judge owns the whole objective.</returns>
        internal static IReadOnlyList<string>? ResolveIntermediateSliceCriteria(Mission judge, IReadOnlyList<Mission> voyageMissions)
        {
            if (judge == null) throw new ArgumentNullException(nameof(judge));
            if (voyageMissions == null) throw new ArgumentNullException(nameof(voyageMissions));
            if (!String.Equals(judge.Persona, "Judge", StringComparison.OrdinalIgnoreCase)) return null;

            Dictionary<string, Mission> byId = new Dictionary<string, Mission>(StringComparer.Ordinal);
            foreach (Mission row in voyageMissions)
            {
                if (row != null && !String.IsNullOrEmpty(row.Id)) byId[row.Id] = row;
            }

            Mission? root = FindOwningSliceRoot(judge, byId);
            if (root == null) return null;
            if (HasIntegrationBarrierState(root)) return null;
            if (!IsFollowedByLaterSlice(judge, root, voyageMissions, byId)) return null;

            // The Judge's description is a rendered prompt that can be budget-truncated, so the block's criteria
            // are read from the slice root, which holds the Architect block verbatim.
            List<string> local = JudgeAcceptanceWalk.ExtractSliceBlockCriteria(root.Description);
            return local.Count == 0 ? null : local;
        }

        private static bool HasIntegrationBarrierState(Mission mission)
        {
            return mission.IntegrationBarrier != null || mission.IntegrationBarrierInvalid || mission.IntegrationBarrierRawJsonPresent;
        }

        // The first stage of the Architect plan block a stage belongs to: the nearest Worker, walking the
        // dependency chain upward, that the fan-out marked as a slice root. Null for a chain that reaches the
        // Architect itself or a missing row without one.
        private static Mission? FindOwningSliceRoot(Mission stage, Dictionary<string, Mission> byId)
        {
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            Mission? current = stage;
            while (current != null && visited.Add(current.Id))
            {
                if (IsArchitectSliceRoot(current)) return current;
                if (String.Equals(current.Persona, "Architect", StringComparison.OrdinalIgnoreCase)) return null;
                if (String.IsNullOrEmpty(current.DependsOnMissionId)) return null;
                byId.TryGetValue(current.DependsOnMissionId, out current);
            }
            return null;
        }

        private static bool IsFollowedByLaterSlice(Mission judge, Mission root, IReadOnlyList<Mission> voyageMissions, Dictionary<string, Mission> byId)
        {
            string? architectId = FindOwningArchitectId(root, byId);
            foreach (Mission other in voyageMissions)
            {
                if (other == null || String.Equals(other.Id, root.Id, StringComparison.Ordinal) || !IsArchitectSliceRoot(other)) continue;

                // Serial plan: the other slice starts after this Judge's stage.
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal) { other.Id };
                string? next = other.DependsOnMissionId;
                while (!String.IsNullOrEmpty(next) && visited.Add(next))
                {
                    if (String.Equals(next, judge.Id, StringComparison.Ordinal)) return true;
                    next = byId.TryGetValue(next, out Mission? parent) ? parent.DependsOnMissionId : null;
                }

                // Parallel plan: the other slice is the integration mission of the same plan, which waits for this one.
                if (other.IntegrationBarrier != null && architectId != null
                    && String.Equals(other.IntegrationBarrier.ArchitectMissionId, architectId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static string? FindOwningArchitectId(Mission start, Dictionary<string, Mission> byId)
        {
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            Mission? current = start;
            while (current != null && visited.Add(current.Id))
            {
                if (String.Equals(current.Persona, "Architect", StringComparison.OrdinalIgnoreCase)) return current.Id;
                if (String.IsNullOrEmpty(current.DependsOnMissionId)) return null;
                byId.TryGetValue(current.DependsOnMissionId, out current);
            }
            return null;
        }

        private async Task<IReadOnlyList<string>?> ReadIntermediateSliceCriteriaAsync(Mission mission, CancellationToken token)
        {
            if (String.IsNullOrEmpty(mission.VoyageId)) return null;
            try
            {
                List<Mission> voyageMissions = await _Database.Missions.EnumerateByVoyageAsync(mission.VoyageId!, token).ConfigureAwait(false);
                return ResolveIntermediateSliceCriteria(mission, voyageMissions);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _Logging.Warn(_Header + "slice scope lookup failed for judge mission " + mission.Id + ", the linked objective's criteria stand: " + e.Message);
                return null;
            }
        }
    }
}
