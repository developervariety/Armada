namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>Null-safe collection defaults for JSON input.</summary>
    public static partial class ArchitectVersionedPlanParser
    {
        private static void NormalizeCollections(VersionedArchitectPlan plan)
        {
            plan.Requirements ??= new List<string>();
            plan.Decisions ??= new List<string>();
            plan.Missions ??= new List<VersionedArchitectPlanMission>();
            foreach (VersionedArchitectPlanMission? mission in plan.Missions)
            {
                if (mission == null) continue;
                mission.AcceptanceCriteria ??= new List<VersionedArchitectAcceptanceCriterion>();
                mission.ValidationCommands ??= new List<string>();
                foreach (VersionedArchitectAcceptanceCriterion? criterion in mission.AcceptanceCriteria)
                {
                    if (criterion == null) continue;
                    criterion.RequirementIds ??= new List<string>();
                }
            }
        }
    }
}
