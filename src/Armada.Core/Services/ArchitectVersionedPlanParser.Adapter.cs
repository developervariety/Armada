namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using Armada.Core.Models;

    /// <summary>Maps a validated versioned plan to the existing parser result.</summary>
    public static partial class ArchitectVersionedPlanParser
    {
        private static ArchitectParseResult Adapt(VersionedArchitectPlan plan, int maxMissions, string selectedJson)
        {
            ArchitectParseResult result = new ArchitectParseResult
            {
                Plan = new ArchitectPlan { FullMarkdown = selectedJson },
                Missions = new List<ArchitectMissionEntry>()
            };
            for (int i = 0; i < plan.Missions.Count; i++)
            {
                VersionedArchitectPlanMission source = plan.Missions[i];
                result.Missions.Add(new ArchitectMissionEntry
                {
                    Id = "M" + (i + 1),
                    Title = source.Title ?? "",
                    Description = DescribeMission(plan, source),
                    DependsOnMissionAlias = source.DependsOn.HasValue ? "M" + source.DependsOn.Value : "",
                    WaitForOtherMissions = source.WaitForOtherMissions
                });
            }
            if (result.Missions.Count > maxMissions)
            {
                result.Verdict = ArchitectParseVerdict.OverCap;
                result.MissionCount = result.Missions.Count;
                result.MaxMissions = maxMissions;
                result.RecommendedSubVoyages = (result.Missions.Count + maxMissions - 1) / maxMissions;
            }
            else result.Verdict = ArchitectParseVerdict.Valid;
            return result;
        }

        private static string DescribeMission(VersionedArchitectPlan plan, VersionedArchitectPlanMission mission)
        {
            StringBuilder text = new StringBuilder(mission.Description ?? "");
            text.Append("\n\n## Plan mission kind\n").Append(mission.Kind).Append('\n');
            text.Append("\n\n## Resolved plan decisions\n");
            IEnumerable<int> decisions = mission.DecisionIndexes ?? Enumerable.Range(1, plan.Decisions.Count);
            foreach (int index in decisions) text.Append("- D").Append(index).Append(": ").Append(plan.Decisions[index - 1]).Append('\n');
            text.Append("\n## Acceptance evidence contract\n");
            foreach (VersionedArchitectAcceptanceCriterion criterion in mission.AcceptanceCriteria)
                AppendCriterion(text, plan, criterion);
            text.Append("\n## Required validation commands\n");
            foreach (string command in mission.ValidationCommands) text.Append("- ").Append(command).Append('\n');
            return text.ToString();
        }

        private static void AppendCriterion(StringBuilder text, VersionedArchitectPlan plan, VersionedArchitectAcceptanceCriterion criterion)
        {
            text.Append("- ").Append(criterion.Id).Append(" [").Append(String.Join(", ", criterion.RequirementIds)).Append("]: ")
                .Append(criterion.Scenario).Append("\n  Expected: ").Append(criterion.Expected)
                .Append("\n  Verify: ").Append(criterion.Verification).Append('\n');
            if (plan.ContractVersion == 3)
                text.Append("  Decisions checked: ").Append(criterion.DecisionIndexes!.Count == 0 ? "none (general check)"
                    : String.Join(", ", criterion.DecisionIndexes.Select(index => "D" + index))).Append('\n');
            foreach (string negative in criterion.NegativeCases!) text.Append("  Failure/boundary: ").Append(negative).Append('\n');
            if (criterion.NegativeCases!.Count == 0) text.Append("  Negative cases not applicable: ").Append(criterion.NegativeCasesNotApplicableReason).Append('\n');
        }
    }
}
