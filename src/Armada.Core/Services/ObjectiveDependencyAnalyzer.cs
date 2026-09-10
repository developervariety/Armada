namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Builds deterministic dependency-readiness results from an objective snapshot.
    /// </summary>
    public static class ObjectiveDependencyAnalyzer
    {
        /// <summary>Maximum diagnostic paths returned with one complete dependency graph.</summary>
        public const int MaxBlockingChains = 100;

        /// <summary>
        /// Analyze all reachable incomplete dependencies for one objective.
        /// </summary>
        /// <param name="objective">Objective to analyze.</param>
        /// <param name="all">Full visible objective snapshot.</param>
        /// <returns>Dependency readiness, graph, terminal paths, and cycle information.</returns>
        public static ObjectiveDependencyAnalysis Analyze(Objective objective, IReadOnlyList<Objective> all)
        {
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            if (all == null) throw new ArgumentNullException(nameof(all));

            Dictionary<string, Objective> byId = all
                .Where(item => item != null && !String.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            byId[objective.Id] = objective;

            ObjectiveDependencyAnalysis result = new ObjectiveDependencyAnalysis
            {
                ObjectiveId = objective.Id
            };
            BuildBlockingGraph(objective, byId, result);
            EnumerateChains(objective.Id, byId, result, new List<string> { objective.Id });
            result.IsDependencyReady = result.BlockingEdges.Count == 0;
            result.BlockingNodes = result.BlockingNodes
                .OrderBy(node => node.ObjectiveId, StringComparer.Ordinal)
                .ToList();
            result.BlockingEdges = result.BlockingEdges
                .OrderBy(edge => edge.DependentObjectiveId, StringComparer.Ordinal)
                .ThenBy(edge => edge.BlockingObjectiveId, StringComparer.Ordinal)
                .ToList();
            result.BlockingChains = result.BlockingChains
                .OrderBy(chain => String.Join("\u001f", chain.ObjectiveIds), StringComparer.Ordinal)
                .ThenBy(chain => chain.TerminalReason)
                .ToList();
            return result;
        }

        /// <summary>
        /// Find a structural dependency cycle that contains the supplied objective. Completed
        /// objectives remain part of this traversal because lifecycle state must not make an invalid
        /// graph valid.
        /// </summary>
        /// <param name="objective">Objective whose outgoing blockers are being validated.</param>
        /// <param name="all">Full visible objective snapshot, including the candidate value.</param>
        /// <returns>Closed cycle path, or an empty list when the objective is not in a cycle.</returns>
        public static List<string> FindStructuralCycle(Objective objective, IReadOnlyList<Objective> all)
        {
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            if (all == null) throw new ArgumentNullException(nameof(all));

            Dictionary<string, Objective> byId = all
                .Where(item => item != null && !String.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            byId[objective.Id] = objective;

            Queue<List<string>> paths = new Queue<List<string>>();
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string blockerId in OrderedBlockers(objective))
            {
                if (String.Equals(blockerId, objective.Id, StringComparison.OrdinalIgnoreCase))
                    return new List<string> { objective.Id, objective.Id };
                if (visited.Add(blockerId)) paths.Enqueue(new List<string> { objective.Id, blockerId });
            }

            while (paths.Count > 0)
            {
                List<string> path = paths.Dequeue();
                string currentId = path[^1];
                if (!byId.TryGetValue(currentId, out Objective? current)) continue;
                foreach (string blockerId in OrderedBlockers(current))
                {
                    if (String.Equals(blockerId, objective.Id, StringComparison.OrdinalIgnoreCase))
                        return path.Concat(new[] { objective.Id }).ToList();
                    if (!visited.Add(blockerId)) continue;
                    paths.Enqueue(path.Concat(new[] { blockerId }).ToList());
                }
            }

            return new List<string>();
        }

        private static void BuildBlockingGraph(
            Objective root,
            Dictionary<string, Objective> byId,
            ObjectiveDependencyAnalysis result)
        {
            Queue<Objective> pending = new Queue<Objective>();
            pending.Enqueue(root);
            HashSet<string> expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Id };
            HashSet<string> nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> edgeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (pending.Count > 0)
            {
                Objective dependent = pending.Dequeue();
                foreach (string blockerId in OrderedBlockers(dependent))
                {
                    byId.TryGetValue(blockerId, out Objective? blocker);
                    if (blocker != null && blocker.Status == ObjectiveStatusEnum.Completed) continue;

                    string edgeKey = dependent.Id + "\u001f" + blockerId;
                    if (edgeKeys.Add(edgeKey))
                    {
                        result.BlockingEdges.Add(new ObjectiveDependencyEdge
                        {
                            DependentObjectiveId = dependent.Id,
                            BlockingObjectiveId = blockerId
                        });
                    }

                    if (nodeIds.Add(blockerId))
                    {
                        result.BlockingNodes.Add(new ObjectiveDependencyNode
                        {
                            ObjectiveId = blockerId,
                            Title = blocker?.Title,
                            Status = blocker?.Status,
                            IsMissing = blocker == null
                        });
                    }

                    if (blocker != null && expanded.Add(blocker.Id)) pending.Enqueue(blocker);
                }
            }
        }

        private static void EnumerateChains(
            string currentId,
            Dictionary<string, Objective> byId,
            ObjectiveDependencyAnalysis result,
            List<string> path)
        {
            if (result.BlockingChainsTruncated) return;
            if (!byId.TryGetValue(currentId, out Objective? current)) return;

            List<string> blockers = OrderedBlockers(current)
                .Where(id => !byId.TryGetValue(id, out Objective? blocker)
                    || blocker.Status != ObjectiveStatusEnum.Completed)
                .ToList();
            foreach (string blockerId in blockers)
            {
                if (result.BlockingChainsTruncated) return;
                byId.TryGetValue(blockerId, out Objective? blocker);
                List<string> nextPath = new List<string>(path) { blockerId };
                if (blocker == null)
                {
                    AddChain(result, nextPath, ObjectiveDependencyTerminalReasonEnum.Missing);
                    continue;
                }

                int cycleStart = path.FindIndex(id => String.Equals(id, blockerId, StringComparison.OrdinalIgnoreCase));
                if (cycleStart >= 0)
                {
                    List<string> cycle = path.Skip(cycleStart).Concat(new[] { blockerId }).ToList();
                    if (!result.HasCycle)
                    {
                        result.HasCycle = true;
                        result.CyclePath = cycle;
                    }
                    AddChain(result, nextPath, ObjectiveDependencyTerminalReasonEnum.Cycle);
                    continue;
                }

                bool hasIncompleteChild = OrderedBlockers(blocker)
                    .Any(id => !byId.TryGetValue(id, out Objective? child)
                        || child.Status != ObjectiveStatusEnum.Completed);
                if (!hasIncompleteChild)
                {
                    AddChain(result, nextPath, ObjectiveDependencyTerminalReasonEnum.Incomplete);
                    continue;
                }

                EnumerateChains(blocker.Id, byId, result, nextPath);
            }
        }

        private static IEnumerable<string> OrderedBlockers(Objective objective)
        {
            return (objective.BlockedByObjectiveIds ?? new List<string>())
                .Where(id => !String.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.Ordinal);
        }

        private static void AddChain(
            ObjectiveDependencyAnalysis result,
            List<string> objectiveIds,
            ObjectiveDependencyTerminalReasonEnum reason)
        {
            if (result.BlockingChains.Count >= MaxBlockingChains)
            {
                result.BlockingChainsTruncated = true;
                return;
            }
            result.BlockingChains.Add(new ObjectiveDependencyChain
            {
                ObjectiveIds = objectiveIds,
                TerminalReason = reason
            });
        }
    }
}
