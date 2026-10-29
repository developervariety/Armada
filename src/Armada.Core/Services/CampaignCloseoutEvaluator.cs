namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json.Serialization;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Checks structural campaign closure without treating links as proof of acceptance criteria.</summary>
    public static class CampaignCloseoutEvaluator
    {
        /// <summary>Inspect every descendant, including levels omitted by a summary tree.</summary>
        /// <param name="rootId">Campaign root.</param>
        /// <param name="objectives">Objectives visible to the caller.</param>
        /// <returns>Remaining closure items and the evidence that needs final review.</returns>
        public static CampaignCloseoutReport Evaluate(string rootId, IReadOnlyList<Objective> objectives)
        {
            if (objectives == null) throw new ArgumentNullException(nameof(objectives));
            CampaignCloseoutReport report = new CampaignCloseoutReport { RootObjectiveId = rootId };
            Objective? root = objectives.FirstOrDefault(row => row.Id == rootId);
            if (root == null)
            {
                report.Items.Add(new CampaignCloseoutItem { ObjectiveId = rootId, Reason = "root_not_found" });
                return report;
            }

            ILookup<string, Objective> children = objectives
                .Where(row => !String.IsNullOrWhiteSpace(row.ParentObjectiveId))
                .ToLookup(row => row.ParentObjectiveId!, StringComparer.OrdinalIgnoreCase);
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Queue<Objective> pending = new Queue<Objective>();
            pending.Enqueue(root);
            while (pending.Count > 0)
            {
                Objective row = pending.Dequeue();
                if (!visited.Add(row.Id))
                {
                    report.Items.Add(new CampaignCloseoutItem { ObjectiveId = row.Id, Reason = "parent_cycle" });
                    continue;
                }
                report.Objectives.Add(row);
                foreach (Objective child in children[row.Id]) pending.Enqueue(child);
            }

            bool HasEvidence(Objective row, HashSet<string> path)
            {
                if (row.Status != ObjectiveStatusEnum.Completed || !path.Add(row.Id)) return false;
                if (row.EvidenceLinks.Any(link => !String.IsNullOrWhiteSpace(link))) return true;
                return children[row.Id].Any(child => HasEvidence(child, new HashSet<string>(path, StringComparer.OrdinalIgnoreCase)));
            }

            foreach (Objective row in report.Objectives)
            {
                if (row.Status != ObjectiveStatusEnum.Completed)
                    report.Items.Add(new CampaignCloseoutItem { ObjectiveId = row.Id, Reason = "objective_not_completed" });
                else if (!HasEvidence(row, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                    report.Items.Add(new CampaignCloseoutItem { ObjectiveId = row.Id, Reason = "completion_evidence_missing" });
            }
            return report;
        }
    }

    /// <summary>Necessary closure conditions; evidence content still requires acceptance review.</summary>
    public sealed class CampaignCloseoutReport
    {
        /// <summary>Root being reviewed.</summary>
        public string RootObjectiveId { get; set; } = String.Empty;
        /// <summary>All scoped rows, including their criteria and evidence links.</summary>
        [JsonIgnore]
        public List<Objective> Objectives { get; set; } = new List<Objective>();
        /// <summary>Only the criterion, source and proof fields needed for final review.</summary>
        public IEnumerable<object> EvidenceReview => Objectives.Select(row => (object)new
        {
            row.Id, row.Title, row.Status, row.AcceptanceCriteria, row.EvidenceLinks,
            row.MissionIds, row.CheckRunIds, row.VoyageIds,
            Source = row.Preparation?.Source, Target = row.Preparation?.Target
        });
        /// <summary>Conditions that prevent campaign completion.</summary>
        public List<CampaignCloseoutItem> Items { get; set; } = new List<CampaignCloseoutItem>();
        /// <summary>True only when every row is Completed with direct or completed-child evidence.</summary>
        public bool ReadyForEvidenceReview => Items.Count == 0;
        /// <summary>Structural closure does not verify the contents of a proof link.</summary>
        public string EvidenceReviewRequirement => "Verify every acceptance criterion against the linked evidence, exact source and required checks. A link or main-branch ancestry alone does not prove completion.";
    }

    /// <summary>A remaining campaign closure condition.</summary>
    public sealed class CampaignCloseoutItem
    {
        /// <summary>Row requiring action.</summary>
        public string ObjectiveId { get; set; } = String.Empty;
        /// <summary>Stable reason for the remaining item.</summary>
        public string Reason { get; set; } = String.Empty;
    }
}
