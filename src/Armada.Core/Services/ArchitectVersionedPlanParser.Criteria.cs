namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Armada.Core.Models;

    /// <summary>Acceptance evidence checks for versioned plan missions.</summary>
    public static partial class ArchitectVersionedPlanParser
    {
        private static void ValidateCriterion(VersionedArchitectPlan plan, VersionedArchitectPlanMission mission,
            VersionedArchitectAcceptanceCriterion? criterion, string prefix, HashSet<string> ids,
            HashSet<string> covered, HashSet<int> checkedDecisions, List<string> errors)
        {
            if (criterion == null) { errors.Add(prefix + "acceptance criterion is null"); return; }
            if (String.IsNullOrWhiteSpace(criterion.Id) || !ids.Add(criterion.Id)) errors.Add(prefix + "criterion IDs must be unique and nonempty");
            if (String.IsNullOrWhiteSpace(criterion.Scenario) || String.IsNullOrWhiteSpace(criterion.Expected)
                || String.IsNullOrWhiteSpace(criterion.Verification)) errors.Add(prefix + "criterion needs scenario, expected, and verification");
            if (criterion.RequirementIds.Count == 0) errors.Add(prefix + "criterion must cover requirements");
            foreach (string id in criterion.RequirementIds)
            {
                if (!plan.Requirements.Contains(id, StringComparer.Ordinal)) errors.Add(prefix + "criterion references unknown requirement " + id);
                else covered.Add(id);
            }
            if (plan.ContractVersion == 3)
            {
                if (criterion.DecisionIndexes == null) errors.Add(prefix + "criterion decisionIndexes required in version 3");
                else
                {
                    CheckIndexes(criterion.DecisionIndexes, plan.Decisions.Count, prefix + "criterion decisionIndexes", errors);
                    foreach (int index in criterion.DecisionIndexes)
                    {
                        if (index > 0 && index <= plan.Decisions.Count
                            && (mission.DecisionIndexes == null || mission.DecisionIndexes.Contains(index))) checkedDecisions.Add(index);
                        else errors.Add(prefix + "criterion decision index is outside its mission scope");
                    }
                }
            }
            bool noCases = criterion.NegativeCases != null && criterion.NegativeCases.Count == 0;
            if (criterion.NegativeCases == null || criterion.NegativeCases.Any(String.IsNullOrWhiteSpace)
                || (noCases && String.IsNullOrWhiteSpace(criterion.NegativeCasesNotApplicableReason)))
                errors.Add(prefix + "negativeCases or a specific non-applicability reason required");
        }
    }
}
