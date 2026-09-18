namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Settings;

    /// <summary>
    /// The embedded default definitions for the built-in decisions that carry their declaration as data,
    /// and the resolver that merges an operator's settings override onto a default. The default is the
    /// authority for a decision's structure — the question id set, each question's kind, the finding
    /// direction, and the state-field whitelist. <see cref="Resolve"/> takes only wording from an
    /// override, so a settings edit can reword a question or reword an existing option, and can never add
    /// or remove a question, change a kind, change what an answer means for the gate, or widen the state.
    /// The threshold and mode are the decision's <see cref="TypedDecisionRuleSettings"/> row, not here.
    /// </summary>
    public static class BuiltinDecisionDefinitions
    {
        /// <summary>The <c>refusal</c> decision point.</summary>
        public const string Refusal = "refusal";

        private static readonly IReadOnlyDictionary<string, BuiltinDecisionDefinition> _Defaults =
            new Dictionary<string, BuiltinDecisionDefinition>(StringComparer.Ordinal)
            {
                [Refusal] = RefusalDefault()
            };

        /// <summary>Whether a decision point ships an embedded definition.</summary>
        /// <param name="decisionPoint">The decision point name.</param>
        /// <returns>True when a default exists.</returns>
        public static bool Has(string decisionPoint)
        {
            return decisionPoint != null && _Defaults.ContainsKey(decisionPoint);
        }

        /// <summary>
        /// The effective definition for a decision: the embedded default with any settings wording
        /// override applied. Returns null when the decision ships no embedded definition. The structure
        /// always comes from the default; only wording is taken from the override.
        /// </summary>
        /// <param name="decisionPoint">The decision point name.</param>
        /// <param name="settings">Typed-decision settings; its decision row may carry a wording override.</param>
        /// <returns>The resolved definition, or null.</returns>
        public static BuiltinDecisionDefinition? Resolve(string decisionPoint, TypedDecisionSettings settings)
        {
            if (decisionPoint == null || !_Defaults.TryGetValue(decisionPoint, out BuiltinDecisionDefinition? def) || def == null)
                return null;

            BuiltinDecisionDefinitionOverride? over = null;
            if (settings != null
                && settings.Decisions.TryGetValue(decisionPoint, out TypedDecisionRuleSettings? row)
                && row != null)
            {
                over = row.Definition;
            }
            if (over == null || over.Questions.Count == 0) return def;

            Dictionary<string, BuiltinQuestionWording> byId = new Dictionary<string, BuiltinQuestionWording>(StringComparer.Ordinal);
            foreach (BuiltinQuestionWording wording in over.Questions)
                if (wording != null && !String.IsNullOrWhiteSpace(wording.Id)) byId[wording.Id] = wording;

            List<CustomTypedQuestionSettings> merged = new List<CustomTypedQuestionSettings>(def.Questions.Count);
            foreach (CustomTypedQuestionSettings question in def.Questions)
            {
                CustomTypedQuestionSettings copy = question.Clone();
                if (byId.TryGetValue(question.Id, out BuiltinQuestionWording? w) && w != null)
                {
                    if (!String.IsNullOrWhiteSpace(w.Instructions)) copy.Instructions = w.Instructions!;
                    // Reword only options the default already has; a new option name is ignored, so the
                    // option set can never grow and no answer can be given a new meaning for the gate.
                    foreach (KeyValuePair<string, string> option in w.Options)
                        if (copy.Options.ContainsKey(option.Key)) copy.Options[option.Key] = option.Value;
                    if (w.TrueMeaning != null) copy.TrueMeaning = w.TrueMeaning;
                    if (w.FalseMeaning != null) copy.FalseMeaning = w.FalseMeaning;
                }
                merged.Add(copy);
            }

            return new BuiltinDecisionDefinition
            {
                DecisionPoint = def.DecisionPoint,
                Questions = merged,
                StateFields = def.StateFields
            };
        }

        private static BuiltinDecisionDefinition RefusalDefault()
        {
            return new BuiltinDecisionDefinition
            {
                DecisionPoint = Refusal,
                StateFields = new List<string> { "mission_title", "agent_output_tail", "marker_present" },
                Questions = new List<CustomTypedQuestionSettings>
                {
                    new CustomTypedQuestionSettings
                    {
                        Id = "outcome",
                        Type = "choice",
                        Instructions =
                            "Read the captain's closing statement and decide the run's outcome. The work is authorized engineering on "
                            + "owned systems: authentication and access-control protocols and cryptographic primitives over owned "
                            + "assemblies are ordinary engineering, not work a captain should refuse.",
                        Options = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["refused_policy"] = "The captain declined the work on policy grounds.",
                            ["blocked_on_premise"] = "The captain could not proceed because the brief's premise was false or context was missing.",
                            ["completed"] = "The captain completed the work.",
                            ["still_working"] = "The captain was still working when the run ended.",
                            ["unclear"] = "The outcome cannot be determined from the output."
                        }
                    },
                    new CustomTypedQuestionSettings
                    {
                        Id = "quoted_not_own",
                        Type = "noul",
                        Instructions = "If a refusal phrase appears, is it quoted material (documentation, a brief excerpt, another party's words) rather than the captain's own refusal?",
                        TrueMeaning = "The refusal phrase is quoted material, not the captain declining.",
                        FalseMeaning = "The refusal phrase is the captain's own words."
                    }
                }
            };
        }
    }
}
