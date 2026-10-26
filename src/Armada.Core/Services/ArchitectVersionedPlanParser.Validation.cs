namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;

    /// <summary>Contract checks for versioned plans.</summary>
    public static partial class ArchitectVersionedPlanParser
    {
        private static List<string> Validate(VersionedArchitectPlan plan)
        {
            List<string> errors = new List<string>();
            if (plan.ContractVersion != 2 && plan.ContractVersion != 3)
            {
                errors.Add("contractVersion must be 2 or 3; unversioned JSON is not supported");
                return errors;
            }
            if (plan.Requirements.Count == 0 || plan.Requirements.Any(String.IsNullOrWhiteSpace)) errors.Add("requirements are required");
            if (plan.Requirements.Distinct(StringComparer.Ordinal).Count() != plan.Requirements.Count) errors.Add("requirement IDs must be unique");
            if (plan.Decisions.Count == 0 || plan.Decisions.Any(String.IsNullOrWhiteSpace)) errors.Add("decisions are required");
            if (plan.Missions.Count == 0) errors.Add("missions are required");

            HashSet<string> titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> criteriaIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> covered = new HashSet<string>(StringComparer.Ordinal);
            HashSet<int> checkedDecisions = new HashSet<int>();
            for (int i = 0; i < plan.Missions.Count; i++)
                ValidateMission(plan, plan.Missions[i], i, titles, criteriaIds, covered, checkedDecisions, errors);

            VersionedArchitectPlanMission final = plan.Missions.LastOrDefault()!;
            if (final != null && (final.Kind != "integration" || !final.WaitForOtherMissions))
                errors.Add("the final mission must be integration with waitForOtherMissions=true");
            foreach (string id in plan.Requirements)
                if (!covered.Contains(id)) errors.Add("requirement " + id + " has no acceptance criterion");
            if (plan.ContractVersion == 3)
                for (int i = 1; i <= plan.Decisions.Count; i++)
                    if (!checkedDecisions.Contains(i)) errors.Add("decision D" + i + " has no named acceptance check");
            return errors;
        }

        private static void ValidateMission(VersionedArchitectPlan plan, VersionedArchitectPlanMission? mission, int i,
            HashSet<string> titles, HashSet<string> ids, HashSet<string> covered, HashSet<int> checkedDecisions, List<string> errors)
        {
            string prefix = "mission " + (i + 1) + ": ";
            if (mission == null) { errors.Add(prefix + "definition is null"); return; }
            if (String.IsNullOrWhiteSpace(mission.Title) || IsPlaceholder(mission.Title)
                || !titles.Add(mission.Title.Trim())) errors.Add(prefix + "real unique title required");
            if (String.IsNullOrWhiteSpace(mission.Description)) errors.Add(prefix + "description required");
            if (mission.Kind != "implementation" && mission.Kind != "investigation" && mission.Kind != "integration") errors.Add(prefix + "invalid kind");
            if (mission.DependsOn.HasValue && (mission.DependsOn.Value < 1 || mission.DependsOn.Value > i)) errors.Add(prefix + "dependsOn must name an earlier mission");
            if (mission.WaitForOtherMissions && (i != plan.Missions.Count - 1 || mission.Kind != "integration")) errors.Add(prefix + "only final integration may wait for other missions");
            CheckIndexes(mission.DecisionIndexes, plan.Decisions.Count, prefix + "decisionIndexes", errors);
            if (mission.ValidationCommands.Count == 0 || mission.ValidationCommands.Any(String.IsNullOrWhiteSpace)) errors.Add(prefix + "validationCommands required");
            if (mission.AcceptanceCriteria.Count == 0) errors.Add(prefix + "acceptanceCriteria required");
            foreach (VersionedArchitectAcceptanceCriterion? criterion in mission.AcceptanceCriteria)
                ValidateCriterion(plan, mission, criterion, prefix, ids, covered, checkedDecisions, errors);
        }

        private static bool IsPlaceholder(string title)
        {
            string trimmed = title.Trim();
            return trimmed.StartsWith("<", StringComparison.Ordinal) && trimmed.EndsWith(">", StringComparison.Ordinal);
        }

        private static void CheckIndexes(List<int>? indexes, int max, string field, List<string> errors)
        {
            if (indexes == null) return;
            if (indexes.Any(index => index < 1 || index > max) || indexes.Distinct().Count() != indexes.Count)
                errors.Add(field + " must contain unique valid 1-based indexes");
        }
    }
}
