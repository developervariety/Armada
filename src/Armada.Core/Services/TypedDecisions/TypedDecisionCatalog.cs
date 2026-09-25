namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>One-line descriptions of the shipped typed decisions, keyed by settings key, for the operator API.</summary>
    public static class TypedDecisionCatalog
    {
        #region Private-Members

        private static readonly IReadOnlyDictionary<string, string> _Descriptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["failure_cause"] = "The class of a mission failure, which governs whether a rescue is warranted.",
            ["refusal"] = "Whether a captain result is a model refusal rather than genuine output.",
            ["runtime_failure"] = "Whether a process or runtime error is recoverable work worth preserving.",
            ["review_substance"] = "Whether a reviewer verdict is substantive rather than an empty or templated pass.",
            ["preflight"] = "Whether a dispatch brief passes the pre-dispatch checks the deterministic preflight cannot express.",
            ["papercut_merge"] = "Whether two papercut reports describe the same underlying issue.",
            ["capacity_escalation"] = "Smart Routing: whether a mission is lighter, default, or stronger work for its persona's model lists.",
            ["dispatch_staleness"] = "Whether a stale code index should proceed, refresh inline, or wait, given the work and the configured policy.",
            ["leak_hunk"] = "Whether a diff hunk may carry private context that must not reach a repository.",
            ["log_watch"] = "Whether a running captain's log shows trouble worth an operator's attention.",
            ["premise_check"] = "Whether a brief's stated premise still holds at the target commit.",
            ["criteria_lint"] = "Whether acceptance criteria are testable and unambiguous.",
            ["inbox_triage"] = "The attention level of an inbox item or coordination note.",
            ["followup_routing"] = "Where a reviewer follow-up belongs: a new row, a blocking voyage, or a note.",
            ["owner_digest"] = "A digest of owner decisions for later review.",
            ["corpus_prelabel"] = "A provisional label for a captured decision in the evaluation corpus.",
            ["flake_score"] = "Whether a single test failure reads as a load flake or a real defect.",
            ["change_substance"] = "Whether a code change is substantive rather than cosmetic.",
            ["memory_candidate"] = "Whether an observation is worth a durable memory.",
            ["stage_necessity"] = "Whether a pipeline stage is necessary for the work.",
            ["handoff_outcome"] = "The outcome class of a stage handoff.",
            ["revision_kind"] = "Whether a revision request names a behaviour change or only comment or wording.",
            ["test_covers"] = "Whether a test actually covers the reported symptom.",
            ["memory_record"] = "The shape of a memory record for a captured decision.",
            ["memory_review"] = "Whether a reviewed memory's salience should be lowered.",
            ["lint_finding"] = "How a Linter finding routes to the next stage.",
            ["prior_art"] = "Whether the requested work already exists before a stage begins.",
            ["memory_relevance"] = "Whether a retrieved memory leaf applies to a mission's work, which sorts it into the brief's read-first or reference files.",
            ["change_quality"] = "A multi-dimension quality read of a focused diff: DRY, cognitive complexity, modularity, readability, and maintainability."
        };

        #endregion

        #region Public-Methods

        /// <summary>The description of a decision, or a note that the name is not a shipped decision.</summary>
        /// <param name="decision">The settings key.</param>
        /// <returns>The description.</returns>
        public static string Describe(string decision)
        {
            return decision != null && _Descriptions.TryGetValue(decision, out string? text) ? text : "Not a shipped decision; stored but never consulted.";
        }

        #endregion
    }
}
