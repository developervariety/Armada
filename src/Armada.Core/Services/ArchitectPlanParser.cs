namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using Armada.Core.Models;

    /// <summary>
    /// Reads the structured plan an Architect captain emits as a fenced code block whose info string is
    /// <c>armada-plan</c>:
    /// <code>
    /// ```armada-plan
    /// {"missions":[{"title":"...","description":"...","dependsOn":null,"waitForOtherMissions":false}]}
    /// ```
    /// </code>
    /// The block is deserialized into <see cref="ArchitectPlan"/>. Versioned contracts are validated before fan-out.
    /// When no such block is present, the caller falls back to the legacy <c>[ARMADA:MISSION]</c>
    /// marker format (documented as a fallback).
    /// </summary>
    public static class ArchitectPlanParser
    {
        #region Public-Members

        /// <summary>
        /// The fenced code block info string that marks an Architect plan.
        /// </summary>
        public const string FenceInfo = "armada-plan";

        #endregion

        #region Private-Members

        private static readonly JsonSerializerOptions _Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Try to read an Architect plan from output. The final plan block must be valid (an Architect that revises
        /// its plan emits the final one last). Missions without a title, with a placeholder title such as "&lt;title&gt;", and repeated titles are
        /// dropped; a
        /// <c>dependsOn</c> that does not name an earlier kept mission is cleared.
        /// </summary>
        /// <param name="output">Architect output.</param>
        /// <param name="plan">The plan, when found.</param>
        /// <returns>True when a plan with at least one titled mission was found.</returns>
        public static bool TryParse(string? output, out ArchitectPlan? plan)
        {
            return TryParse(output, out plan, out _);
        }

        /// <summary>Parse a plan and report actionable contract errors. Invalid versioned plans never fall back to prose.</summary>
        public static bool TryParse(string? output, out ArchitectPlan? plan, out string? error)
        {
            plan = null;
            error = null;
            if (String.IsNullOrWhiteSpace(output)) return false;
            foreach (string block in ExtractPlanBlocks(output))
            {
                ArchitectPlan? candidate;
                try { candidate = JsonSerializer.Deserialize<ArchitectPlan>(block, _Options); }
                catch (JsonException)
                {
                    // A later malformed revision must not silently dispatch an older plan.
                    plan = null;
                    error = "Architect plan JSON is invalid. Emit a complete armada-plan block with numeric dependencies.";
                    continue;
                }
                try { CollectionBounds.Validate(candidate); }
                catch (CollectionLimitExceededException limit)
                {
                    // An oversized plan is rejected (never truncated): the Architect must split the work.
                    plan = null;
                    error = "Architect plan rejected: " + limit.Message + " Split the work into smaller plans.";
                    continue;
                }
                if (candidate?.ContractVersion != null)
                {
                    List<string> errors = ValidateContract(candidate);
                    plan = errors.Count == 0 ? candidate : null;
                    error = errors.Count == 0 ? null : "Architect plan contract rejected: " + String.Join("; ", errors)
                        + ". Remediation: correct the plan before dispatching Workers; retain objective requirement IDs and explicit failure-case evidence.";
                }
                else
                {
                    plan = Normalize(candidate);
                    error = plan == null ? "Architect plan contains no valid missions." : null;
                }
            }
            return plan != null;
        }

        /// <summary>Render validated structured criteria into the persisted slice, so every downstream stage sees the same contract.</summary>
        public static string DescribeMission(ArchitectPlan plan, ArchitectPlanMission mission)
        {
            if (plan.ContractVersion != 2 && plan.ContractVersion != 3) return mission.Description ?? mission.Title ?? String.Empty;
            StringBuilder text = new StringBuilder(mission.Description);
            text.Append("\n\n## Resolved plan decisions\n");
            foreach (int index in mission.DecisionIndexes ?? Enumerable.Range(1, plan.Decisions.Count).ToList())
                text.Append("- D").Append(index).Append(": ").Append(plan.Decisions[index - 1]).Append('\n');
            text.Append("\n## Acceptance evidence contract\n");
            foreach (ArchitectAcceptanceCriterion criterion in mission.AcceptanceCriteria)
            {
                text.Append("- ").Append(criterion.Id).Append(" [").Append(String.Join(", ", criterion.RequirementIds))
                    .Append("]: ").Append(criterion.Scenario).Append("\n  Expected: ").Append(criterion.Expected)
                    .Append("\n  Verify: ").Append(criterion.Verification).Append('\n');
                if (plan.ContractVersion == 3) text.Append("  Decisions checked: ").Append(criterion.DecisionIndexes!.Count == 0
                    ? "none (general check)" : String.Join(", ", criterion.DecisionIndexes.Select(index => "D" + index))).Append('\n');
                foreach (string negative in criterion.NegativeCases) text.Append("  Failure/boundary: ").Append(negative).Append('\n');
                if (criterion.NegativeCases.Count == 0) text.Append("  Negative cases not applicable: ").Append(criterion.NegativeCasesNotApplicableReason).Append('\n');
            }
            text.Append("\n## Required validation commands\n");
            foreach (string command in mission.ValidationCommands) text.Append("- ").Append(command).Append('\n');
            text.Append("For every acceptance ID and linked decision, report the named check, observed result, and unverified paths. "
                + "Attach request capture before the action and prove it observes requests. Preserve original command exit status; "
                + "test counts and inherited claims do not establish an omitted guarantee.\n");
            return text.ToString();
        }

        private static List<string> ValidateContract(ArchitectPlan plan)
        {
            List<string> errors = new List<string>();
            if (plan.ContractVersion != 2 && plan.ContractVersion != 3) { errors.Add("unsupported contractVersion (expected 2 or 3)"); return errors; }
            if (plan.Missions == null || plan.Missions.Count == 0) { errors.Add("missions are required"); return errors; }
            if (plan.Requirements == null || plan.Requirements.Count == 0 || plan.Requirements.Any(String.IsNullOrWhiteSpace))
                errors.Add("nonempty objective requirements are required");
            HashSet<string> requirements = new HashSet<string>(plan.Requirements ?? new List<string>(), StringComparer.Ordinal);
            if (requirements.Count != plan.Requirements?.Count) errors.Add("objective requirement IDs must be unique");
            if (plan.Decisions == null || plan.Decisions.Count == 0 || plan.Decisions.Any(String.IsNullOrWhiteSpace))
                errors.Add("resolved design decisions are required (including compatibility and explicit settings)");
            HashSet<int> checkedDecisions = new HashSet<int>();
            HashSet<string> covered = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> unknownRequirements = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < plan.Missions.Count; i++)
            {
                ArchitectPlanMission? mission = plan.Missions[i];
                string prefix = "mission " + (i + 1) + ": ";
                if (mission == null) { errors.Add(prefix + "definition is null"); continue; }
                if (String.IsNullOrWhiteSpace(mission.Title) || IsPlaceholder(mission.Title) || !titles.Add(mission.Title.Trim())) errors.Add(prefix + "real unique title required");
                if (String.IsNullOrWhiteSpace(mission.Description)) errors.Add(prefix + "bounded description required");
                if (mission.Kind != "implementation" && mission.Kind != "investigation" && mission.Kind != "integration") errors.Add(prefix + "kind must be implementation, investigation or integration");
                if (mission.DependsOn.HasValue && (mission.DependsOn.Value < 1 || mission.DependsOn.Value > i)) errors.Add(prefix + "dependsOn must name an earlier mission");
                if (mission.WaitForOtherMissions && i != plan.Missions.Count - 1) errors.Add(prefix + "only final integration may wait for all missions");
                if (mission.DecisionIndexes != null && (mission.DecisionIndexes.Any(index => index < 1 || index > (plan.Decisions?.Count ?? 0))
                    || mission.DecisionIndexes.Distinct().Count() != mission.DecisionIndexes.Count)) errors.Add(prefix + "decisionIndexes must be unique 1-based plan decision indexes");
                if (mission.ValidationCommands == null || mission.ValidationCommands.Count == 0 || mission.ValidationCommands.Any(String.IsNullOrWhiteSpace)) errors.Add(prefix + "validationCommands are required");
                if (mission.AcceptanceCriteria == null || mission.AcceptanceCriteria.Count == 0) { errors.Add(prefix + "acceptanceCriteria are required"); continue; }
                foreach (ArchitectAcceptanceCriterion? criterion in mission.AcceptanceCriteria)
                {
                    if (criterion == null) { errors.Add(prefix + "acceptance criterion is null"); continue; }
                    if (String.IsNullOrWhiteSpace(criterion.Id) || !ids.Add(criterion.Id)) errors.Add(prefix + "acceptance IDs must be nonempty and unique");
                    if (String.IsNullOrWhiteSpace(criterion.Scenario) || String.IsNullOrWhiteSpace(criterion.Expected) || String.IsNullOrWhiteSpace(criterion.Verification)) errors.Add(prefix + "each criterion needs scenario, expected and verification");
                    if (criterion.RequirementIds == null || criterion.RequirementIds.Count == 0) errors.Add(prefix + "each criterion must cover objective requirement IDs");
                    else foreach (string id in criterion.RequirementIds)
                    {
                        if (id == null || !requirements.Contains(id))
                        {
                            string referenced = JsonSerializer.Serialize(id);
                            if (unknownRequirements.Add(referenced))
                                errors.Add(prefix + "criterion " + criterion.Id + " references unknown objective requirement ID " + referenced
                                    + "; requirements contains IDs only (for example \"REQ-1\", not \"REQ-1: explanation\"); requirementIds must match exactly and explanations belong in descriptions");
                        }
                        else covered.Add(id);
                    }
                    if (plan.ContractVersion == 3)
                    {
                        if (criterion.DecisionIndexes == null) errors.Add(prefix + "criterion " + criterion.Id + " requires decisionIndexes ([] for a general check)");
                        else
                        {
                            if (criterion.DecisionIndexes.Distinct().Count() != criterion.DecisionIndexes.Count)
                                errors.Add(prefix + "criterion " + criterion.Id + " decisionIndexes must be unique");
                            foreach (int index in criterion.DecisionIndexes)
                            {
                                if (index < 1 || index > (plan.Decisions?.Count ?? 0))
                                    errors.Add(prefix + "criterion " + criterion.Id + " decisionIndexes must name 1-based plan decisions");
                                else if (mission.DecisionIndexes != null && !mission.DecisionIndexes.Contains(index))
                                    errors.Add(prefix + "criterion " + criterion.Id + " references D" + index + " outside this slice's decisionIndexes");
                                else checkedDecisions.Add(index);
                            }
                        }
                    }
                    if (criterion.NegativeCases == null || criterion.NegativeCases.Any(String.IsNullOrWhiteSpace)
                        || (criterion.NegativeCases.Count == 0 && String.IsNullOrWhiteSpace(criterion.NegativeCasesNotApplicableReason)))
                        errors.Add(prefix + "negativeCases or a specific non-applicability reason required");
                }
            }
            ArchitectPlanMission? final = plan.Missions[plan.Missions.Count - 1];
            if (final?.Kind != "integration" || !final.WaitForOtherMissions) errors.Add("last mission must be integration with waitForOtherMissions=true");
            foreach (string requirement in requirements.Where(r => !covered.Contains(r))) errors.Add("uncovered objective requirement " + requirement);
            if (plan.ContractVersion == 3)
                for (int index = 1; index <= (plan.Decisions?.Count ?? 0); index++)
                    if (!checkedDecisions.Contains(index)) errors.Add("decision D" + index + " has no acceptance check; link it to a named verification before dispatch");
            return errors;
        }

        /// <summary>
        /// One-sentence output contract for Architect prompts.
        /// </summary>
        /// <returns>Contract text.</returns>
        public static string FormatInstructions()
        {
            return "Respond with one fenced ```" + FenceInfo + " JSON block using contractVersion:3, requirements:[\"REQ-1\"], decisions:[atomic resolved compatibility/default/override rules], " +
                "missions:[{title,description,kind:implementation|investigation|integration,dependsOn:earlier 1-based index or null,waitForOtherMissions:false," +
                "decisionIndexes:[1-based indexes of only relevant decisions; empty if none],validationCommands:[exact commands],acceptanceCriteria:[{id,requirementIds:[objective IDs],decisionIndexes:[plan decisions verified by this check; [] for general checks],scenario,expected,verification,negativeCases:[inputs and expected failures],negativeCasesNotApplicableReason:only if none apply}]}]. " +
                "requirements contains IDs only, never \"REQ-1: explanation\"; put requirement text in descriptions. Each requirementIds entry must match exactly a requirements string, including case. " +
                "REQ-1 is illustrative: retain every ID assigned by the objective or Product Manager, not this single example. If upstream supplies no IDs, assign one per independent objective requirement. " +
                "Challenge interacting invariants with concrete counterexamples, including user input equal to a fixed output marker and partial fragments outside matched spans. Resolve conflicts against the original objective before implementation; do not weaken it. " +
                "Descriptions define one bounded outcome, components, approach, scope exclusions, invariants, dependencies and uncertainties. Split compound decisions into separately verifiable rules. Every objective ID and decision must have a named acceptance check. Criterion decision indexes must be within its slice scope. " +
                "Resolve correctness-critical uncertainty before implementation. Last mission must be integration with waitForOtherMissions:true. Invalid versioned plans are rejected before dispatch; " +
                "legacy [ARMADA:MISSION] blocks remain supported for customized templates.";
        }

        #endregion

        #region Private-Methods

        private static List<string> ExtractPlanBlocks(string output)
        {
            List<string> blocks = new List<string>();
            string[] lines = output.Replace("\r\n", "\n").Split('\n');
            StringBuilder? current = null;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (current == null)
                {
                    if (line.StartsWith("```", StringComparison.Ordinal)
                        && String.Equals(line.Substring(3).Trim(), FenceInfo, StringComparison.OrdinalIgnoreCase))
                    {
                        current = new StringBuilder();
                    }

                    continue;
                }

                if (line.StartsWith("```", StringComparison.Ordinal))
                {
                    blocks.Add(current.ToString());
                    current = null;
                    continue;
                }

                current.AppendLine(rawLine);
            }

            return blocks;
        }

        private static bool IsPlaceholder(string title)
        {
            // The prompt's own example uses angle-bracket placeholders such as "<title>"; an echoed example is not a plan.
            string trimmed = title.Trim();
            return trimmed.StartsWith("<", StringComparison.Ordinal) && trimmed.EndsWith(">", StringComparison.Ordinal);
        }

        private static ArchitectPlan? Normalize(ArchitectPlan? candidate)
        {
            if (candidate == null || candidate.Missions == null) return null;

            // Map each original 1-based position to its position after untitled entries are dropped, so dependsOn
            // keeps naming the mission the Architect meant.
            Dictionary<int, int> positionMap = new Dictionary<int, int>();
            HashSet<string> seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ArchitectPlan normalized = new ArchitectPlan();
            for (int i = 0; i < candidate.Missions.Count; i++)
            {
                ArchitectPlanMission? mission = candidate.Missions[i];
                if (mission == null || String.IsNullOrWhiteSpace(mission.Title)) continue;
                if (IsPlaceholder(mission.Title)) continue;
                if (!seenTitles.Add(mission.Title.Trim())) continue;

                ArchitectPlanMission copy = new ArchitectPlanMission();
                copy.Title = mission.Title.Trim();
                copy.Description = String.IsNullOrWhiteSpace(mission.Description) ? copy.Title : mission.Description.Trim();
                copy.WaitForOtherMissions = mission.WaitForOtherMissions;

                int originalPosition = i + 1;
                if (mission.DependsOn.HasValue
                    && mission.DependsOn.Value < originalPosition
                    && positionMap.TryGetValue(mission.DependsOn.Value, out int mapped))
                {
                    copy.DependsOn = mapped;
                }

                normalized.Missions.Add(copy);
                positionMap[originalPosition] = normalized.Missions.Count;
            }

            return normalized.Missions.Count > 0 ? normalized : null;
        }

        #endregion
    }
}
