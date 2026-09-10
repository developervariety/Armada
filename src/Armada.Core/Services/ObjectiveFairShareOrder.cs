namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Builds deterministic campaign round-robin order without crossing priority bands.
    /// </summary>
    public static class ObjectiveFairShareOrder
    {
        /// <summary>
        /// Interleave campaign roots within each priority band. Candidate order inside one
        /// campaign remains the selector's rank-and-ID order.
        /// </summary>
        public static List<Objective> Apply(
            IReadOnlyList<Objective> candidates,
            IReadOnlyList<Objective> allObjectives,
            IReadOnlyDictionary<ObjectivePriorityEnum, string> lastServedCampaignByPriority,
            out Dictionary<string, string> campaignByObjectiveId)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (allObjectives == null) throw new ArgumentNullException(nameof(allObjectives));
            if (lastServedCampaignByPriority == null) throw new ArgumentNullException(nameof(lastServedCampaignByPriority));

            Dictionary<string, Objective> byId = allObjectives
                .Where(objective => !String.IsNullOrWhiteSpace(objective.Id))
                .GroupBy(objective => objective.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            Dictionary<string, string> resolvedCampaigns = candidates.ToDictionary(
                objective => objective.Id,
                objective => ResolveCampaignKey(objective, byId),
                StringComparer.Ordinal);
            campaignByObjectiveId = resolvedCampaigns;

            List<Objective> ordered = new List<Objective>(candidates.Count);
            foreach (IGrouping<ObjectivePriorityEnum, Objective> priorityBand in candidates
                .GroupBy(objective => objective.Priority)
                .OrderBy(group => group.Key))
            {
                Dictionary<string, Queue<Objective>> campaigns = priorityBand
                    .GroupBy(objective => resolvedCampaigns[objective.Id], StringComparer.Ordinal)
                    .ToDictionary(
                        group => group.Key,
                        group => new Queue<Objective>(group
                            .OrderBy(objective => objective.Rank)
                            .ThenBy(objective => objective.Id, StringComparer.Ordinal)),
                        StringComparer.Ordinal);
                List<string> campaignOrder = campaigns
                    .OrderBy(pair => pair.Value.Peek().Rank)
                    .ThenBy(pair => pair.Value.Peek().Id, StringComparer.Ordinal)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => pair.Key)
                    .ToList();
                if (lastServedCampaignByPriority.TryGetValue(priorityBand.Key, out string? lastServed))
                {
                    int priorIndex = campaignOrder.IndexOf(lastServed);
                    if (priorIndex >= 0)
                        campaignOrder = campaignOrder.Skip(priorIndex + 1).Concat(campaignOrder.Take(priorIndex + 1)).ToList();
                }

                bool added;
                do
                {
                    added = false;
                    foreach (string campaign in campaignOrder)
                    {
                        if (campaigns[campaign].Count == 0) continue;
                        ordered.Add(campaigns[campaign].Dequeue());
                        added = true;
                    }
                }
                while (added);
            }

            return ordered;
        }

        private static string ResolveCampaignKey(Objective objective, IReadOnlyDictionary<string, Objective> byId)
        {
            Objective current = objective;
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                if (!visited.Add(current.Id)) return "plain";
                if (current.Tags.Any(tag => tag.StartsWith("campaign:", StringComparison.OrdinalIgnoreCase)))
                    return "campaign:" + current.Id;
                if (String.IsNullOrWhiteSpace(current.ParentObjectiveId)) return "plain";

                string parentId = current.ParentObjectiveId;
                if (!byId.TryGetValue(parentId, out Objective? parent)) return "plain";
                current = parent;
            }
        }
    }
}
