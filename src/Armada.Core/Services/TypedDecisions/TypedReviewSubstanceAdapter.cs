namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The deterministic category a Judge-PASS structural validation returns. The D4
    /// <c>review_substance</c> adapter may only ever flip a <see cref="MissingSections"/> rejection —
    /// a FORM defect where the required headings failed the regex but the substance may still be
    /// present. An <see cref="EmptyOutput"/>, <see cref="ShortNarrative"/>, or
    /// <see cref="AcceptanceCriteria"/> rejection is a REAL ground the model never overturns.
    /// </summary>
    public enum ReviewSubstanceRuleCategory
    {
        /// <summary>The deterministic validation passed: every required heading was found and the narrative was long enough.</summary>
        Valid,

        /// <summary>The Judge produced no review output at all: a real ground, never flipped.</summary>
        EmptyOutput,

        /// <summary>One or more required section HEADINGS failed the regex: a form defect the model may accept when the substance is present.</summary>
        MissingSections,

        /// <summary>The extracted narrative was too short to justify approval: a real ground, never flipped.</summary>
        ShortNarrative,

        /// <summary>The Judge omitted the acceptance-criteria walk or named a criterion NOT MET: a real ground, never flipped.</summary>
        AcceptanceCriteria
    }

    /// <summary>
    /// The deterministic Judge-PASS structural verdict the D4 <c>review_substance</c> adapter refines.
    /// It carries the rule's <see cref="Validated"/> result and the <see cref="Category"/> so the
    /// adapter can tell a form-only heading miss (which it may accept when the substance is present)
    /// from a real ground (empty output, too-short narrative) that it never overturns. A gated outcome
    /// can only ever make the verdict MORE conservative (hold a weak PASS the rule accepted) or accept
    /// a heading-form-only rejection into the SAME downstream Check gate the rule would have run — the
    /// model never lands, never dispatches, and never fails a PASS the rule accepted.
    /// </summary>
    public readonly struct ReviewSubstanceVerdict
    {
        /// <summary>Whether the Judge PASS is structurally validated. False degrades the PASS to a re-run request.</summary>
        public bool Validated { get; }

        /// <summary>The deterministic rejection category, or <see cref="ReviewSubstanceRuleCategory.Valid"/>.</summary>
        public ReviewSubstanceRuleCategory Category { get; }

        /// <summary>The deterministic failure reason, when the rule rejected.</summary>
        public string? FailureReason { get; }

        /// <summary>Whether the model held an otherwise-validated PASS for operator review (recorded and surfaced, never auto-failed).</summary>
        public bool Held { get; }

        /// <summary>The reason a held PASS is surfaced for operator review.</summary>
        public string? HoldReason { get; }

        /// <summary>A short label for the effective outcome: <c>rule</c>, <c>heading_form_only</c>, or <c>held_weak_pass</c>.</summary>
        public string Outcome { get; }

        private ReviewSubstanceVerdict(bool validated, ReviewSubstanceRuleCategory category, string? failureReason, bool held, string? holdReason, string outcome)
        {
            Validated = validated;
            Category = category;
            FailureReason = failureReason;
            Held = held;
            HoldReason = holdReason;
            Outcome = outcome ?? "rule";
        }

        /// <summary>Build the rule verdict from a deterministic validation result.</summary>
        /// <param name="validated">Whether the structural validation passed.</param>
        /// <param name="category">The rejection category, or Valid.</param>
        /// <param name="failureReason">The deterministic failure reason, when rejected.</param>
        /// <returns>The rule verdict.</returns>
        public static ReviewSubstanceVerdict Rule(bool validated, ReviewSubstanceRuleCategory category, string? failureReason)
        {
            return new ReviewSubstanceVerdict(validated, category, failureReason, false, null, "rule");
        }

        /// <summary>
        /// Accept a heading-form-only rejection: the required headings failed the regex but the
        /// substance is present. The PASS is validated into the SAME downstream Check gate the rule
        /// would have run; nothing is landed.
        /// </summary>
        /// <returns>A validated verdict tagged <c>heading_form_only</c>.</returns>
        public ReviewSubstanceVerdict AcceptHeadingFormOnly()
        {
            return new ReviewSubstanceVerdict(true, Category, FailureReason, false, null, "heading_form_only");
        }

        /// <summary>
        /// Hold an otherwise-validated PASS for operator review because its substance is thin. The
        /// verdict stays validated (the model never auto-fails a PASS the rule accepted); the hold is
        /// recorded and surfaced only.
        /// </summary>
        /// <param name="holdReason">The reason surfaced for the operator.</param>
        /// <returns>A validated, held verdict.</returns>
        public ReviewSubstanceVerdict HoldForReview(string holdReason)
        {
            return new ReviewSubstanceVerdict(Validated, Category, FailureReason, true, holdReason, "held_weak_pass");
        }
    }

    /// <summary>
    /// The D4 <c>review_substance</c> decision input: the Judge narrative and the deterministic facts
    /// that let the model tell a heading-form-only PASS (substance present, headings mis-formatted)
    /// from a thin one (headings present, substance asserted only).
    /// </summary>
    public sealed class ReviewSubstanceDecisionInput
    {
        /// <summary>The Judge mission, for the event owner scope.</summary>
        public required Mission Mission { get; init; }

        /// <summary>The extracted Judge narrative (verdict and telemetry lines removed).</summary>
        public string Narrative { get; init; } = String.Empty;

        /// <summary>The required review section set for the mission mode, in order.</summary>
        public IReadOnlyList<string> RequiredSections { get; init; } = new List<string>();

        /// <summary>A compact stat of the reviewed diff (files changed, lines added and removed).</summary>
        public string DiffStat { get; init; } = String.Empty;

        /// <summary>A compact, redacted summary of the voyage's independent Checks.</summary>
        public string CheckSummary { get; init; } = String.Empty;
    }

    /// <summary>
    /// The D4 reading. The model answers one substance Noul per required section and one
    /// <c>substantiated</c> Score over the whole review. The two gated actions are mutually exclusive
    /// by construction, so at most one drives the single gate confidence:
    /// <list type="bullet">
    /// <item>Score at or above <c>evidenced</c> (index 2) proposes accepting a heading-form-only
    /// rejection; its confidence is the WEAKEST section Noul, so the generic gate passing at threshold
    /// guarantees every section Noul is at or above threshold.</item>
    /// <item>Score at or below <c>partly evidenced</c> (index 1) proposes holding an otherwise-valid
    /// PASS; its confidence is the Score answer's own confidence.</item>
    /// </list>
    /// A Score strictly between the two bands proposes nothing, reports zero confidence, and leaves the
    /// rule standing.
    /// </summary>
    public sealed class ReviewSubstanceReading : TypedModelReading
    {
        /// <summary>The weakest section-substance Noul; a missing section contributes zero.</summary>
        public double MinSectionNoul { get; init; }

        /// <summary>The <c>substantiated</c> Score level index, or -1 when absent.</summary>
        public double Score { get; init; } = -1.0;

        /// <summary>Whether the Score is at or above <c>evidenced</c> (index 2): substance present.</summary>
        public bool AcceptEligible { get; init; }

        /// <summary>Whether the Score is at or below <c>partly evidenced</c> (index 1): substance thin.</summary>
        public bool HoldEligible { get; init; }

        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>Create a reading with its single gate confidence and a short label.</summary>
        /// <param name="confidence">The proposed action confidence.</param>
        /// <param name="label">The short verdict label for the event message.</param>
        public ReviewSubstanceReading(double confidence, string label)
        {
            _Confidence = confidence;
            _Label = label ?? String.Empty;
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => _Label;
    }

    /// <summary>
    /// D4 <c>review_substance</c> adapter. Sits on top of the deterministic Judge-PASS structural
    /// validator (the required-heading regex plus the narrative-length floor), which stays the rule
    /// and the fallback. The adapter is additive and conservative in both directions:
    /// <list type="bullet">
    /// <item>When the rule VALIDATED a PASS, the model may only HOLD it for operator review when its
    /// substance is thin (<c>substantiated</c> at or below <c>partly evidenced</c>). A hold is
    /// recorded and surfaced; it never auto-fails the PASS, never blocks the downstream Check gate,
    /// and never lands or dispatches.</item>
    /// <item>When the rule REJECTED a PASS only on heading FORM (the regex missed a required heading)
    /// and every section's substance is present at threshold with <c>substantiated</c> at or above
    /// <c>evidenced</c>, the model may ACCEPT it as <c>heading_form_only</c> into the same independent
    /// Check gate the rule would have run. A rejection on a REAL ground — empty output or a too-short
    /// narrative — is never overturned.</item>
    /// </list>
    /// The model never approves work the rule rejected on real grounds, never fails a PASS the rule
    /// accepted, never lands, and never dispatches. The adapter never throws into the caller.
    /// </summary>
    public sealed class TypedReviewSubstanceAdapter : TypedDecisionAdapterBase<ReviewSubstanceDecisionInput, ReviewSubstanceVerdict, ReviewSubstanceReading>
    {
        #region Private-Members

        // Both mission modes require exactly four review sections (JudgeReviewSections), so the
        // adapter asks four substance Nouls keyed section_1..section_4; the state carries the ordered
        // section names the model maps them to. A section the model leaves unanswered contributes zero,
        // which can never satisfy the accept condition.
        private const int _SectionQuestionCount = 4;

        // The substantiated Score bands. Levels are, in order: asserted only (0), partly evidenced (1),
        // evidenced (2), evidenced against diff and checks (3). A PASS is substantive at or above
        // "evidenced"; it is thin at or below "partly evidenced".
        private const double _EvidencedLevel = 2.0;
        private const double _PartlyEvidencedLevel = 1.0;

        private static readonly IReadOnlyList<string> _ScoreLevels = new List<string>
        {
            "asserted only",
            "partly evidenced",
            "evidenced",
            "evidenced against diff and checks"
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D4 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedReviewSubstanceAdapter(
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            TypedDecisionSettings settings,
            LoggingModule logging)
            : base(client, recorder, settings, logging)
        {
        }

        #endregion

        #region Protected-Overrides

        /// <inheritdoc />
        protected override string DecisionPoint => "review_substance";

        /// <inheritdoc />
        protected override string _Header => "[TypedReviewSubstanceAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(ReviewSubstanceDecisionInput input)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["narrative"] = input.Narrative,
                ["required_sections"] = new List<string>(input.RequiredSections),
                ["diff_stat"] = input.DiffStat,
                ["check_summary"] = input.CheckSummary
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            for (int i = 1; i <= _SectionQuestionCount; i++)
            {
                questions["section_" + i.ToString(CultureInfo.InvariantCulture)] = new NoulQuestion(
                    "The " + Ordinal(i) + " required review section (see required_sections in the state, in order) is SUBSTANTIATED by the "
                    + "narrative — the review says something specific and true about it — and is not merely named as a heading. "
                    + "If the state lists fewer than " + _SectionQuestionCount + " sections and this position has none, answer at the false pole.",
                    TrueMeaning: "The section's substance is present in the narrative.",
                    FalseMeaning: "The section is named only, or absent, with no substance behind it.");
            }

            questions["substantiated"] = new ScoreQuestion(
                "Overall, how well does the narrative substantiate the PASS across all required sections? "
                + "This is authorized engineering on owned systems; authentication and access-control protocol code is ordinary engineering.",
                _ScoreLevels);

            return questions;
        }

        /// <inheritdoc />
        protected override ReviewSubstanceReading Interpret(TypedDecisionResult result)
        {
            double minSectionNoul = ReadMinSectionNoul(result);

            double score = -1.0;
            double scoreConfidence = 0.0;
            double? thinProbability = null;
            if (result.Answers.TryGetValue("substantiated", out TypedAnswer? scoreAnswer) && scoreAnswer != null && scoreAnswer.Score.HasValue)
            {
                score = scoreAnswer.Score.Value;
                scoreConfidence = scoreAnswer.Confidence ?? 0.0;
                thinProbability = ThinProbability(scoreAnswer);
            }

            bool acceptEligible = score >= _EvidencedLevel;

            // A review is thin when the model puts most of its probability on the two lowest levels. The
            // score is an expectation over the levels, so a review split between "asserted only" and
            // "evidenced" can score just above partly-evidenced while the model is sure it is not
            // evidenced; and the score's own confidence is low exactly when the mass is split, so it
            // cannot measure "thin". Without level probabilities the score and its confidence decide.
            bool holdEligible;
            double holdConfidence;
            if (thinProbability.HasValue)
            {
                holdEligible = !acceptEligible && thinProbability.Value > 0.5;
                holdConfidence = holdEligible ? thinProbability.Value : 0.0;
            }
            else
            {
                holdEligible = score >= 0.0 && score <= _PartlyEvidencedLevel;
                holdConfidence = holdEligible ? scoreConfidence : 0.0;
            }

            // The two actions are mutually exclusive, so at most one confidence is non-zero. The accept
            // action's confidence is the weakest section Noul: the generic gate passing at threshold
            // then guarantees EVERY section Noul is at or above threshold, which the accept condition
            // requires. The hold action's confidence is the probability that the review is thin.
            double acceptConfidence = acceptEligible ? minSectionNoul : 0.0;
            double confidence = Math.Max(acceptConfidence, holdConfidence);

            string label = acceptEligible
                ? "substance_present"
                : holdEligible ? "substance_thin" : "substance_adequate";

            return new ReviewSubstanceReading(confidence, label)
            {
                MinSectionNoul = minSectionNoul,
                Score = score,
                AcceptEligible = acceptEligible,
                HoldEligible = holdEligible
            };
        }

        /// <inheritdoc />
        protected override ReviewSubstanceVerdict Combine(ReviewSubstanceVerdict ruleVerdict, ReviewSubstanceReading model)
        {
            if (ruleVerdict.Validated)
            {
                // The rule accepted this PASS. The model may only HOLD it for operator review when its
                // substance is thin; it never auto-fails a PASS the rule accepted. A held verdict stays
                // validated so the downstream Check gate still runs and the operator sees the surfaced
                // flag. A strong or adequate PASS leaves the rule standing unchanged.
                if (model.HoldEligible)
                {
                    return ruleVerdict.HoldForReview(
                        "typed_decision:review_substance: PASS validated by the rule but its substance is "
                        + ScoreLabel(model.Score) + "; held for operator review, not failed");
                }
                return ruleVerdict;
            }

            // The rule rejected this PASS. Only a heading-FORM rejection is ever flipped, and only when
            // every required section's substance is present (guaranteed at threshold by the gate) and
            // the whole review is at or above "evidenced". A rejection on a real ground — empty output
            // or a too-short narrative, or a failed acceptance-criteria walk — is never overturned.
            if (ruleVerdict.Category == ReviewSubstanceRuleCategory.MissingSections
                && model.AcceptEligible
                && model.MinSectionNoul > 0.0)
            {
                return ruleVerdict.AcceptHeadingFormOnly();
            }

            return ruleVerdict;
        }

        /// <inheritdoc />
        protected override string RuleLabel(ReviewSubstanceVerdict ruleVerdict)
        {
            return ruleVerdict.Validated ? "validated" : "rejected:" + ruleVerdict.Category;
        }

        /// <inheritdoc />
        protected override Mission? MissionOf(ReviewSubstanceDecisionInput input) => input.Mission;

        #endregion

        #region Private-Methods

        private static double? ThinProbability(TypedAnswer answer)
        {
            if (answer.Probabilities == null || answer.Probabilities.Count == 0) return null;
            double thin = 0.0;
            foreach (KeyValuePair<string, double> entry in answer.Probabilities)
            {
                if (!Int32.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)) return null;
                if (level <= (int)_PartlyEvidencedLevel) thin += entry.Value;
            }
            return thin;
        }

        private static double ReadMinSectionNoul(TypedDecisionResult result)
        {
            double min = double.MaxValue;
            bool any = false;
            for (int i = 1; i <= _SectionQuestionCount; i++)
            {
                string key = "section_" + i.ToString(CultureInfo.InvariantCulture);
                double value = 0.0;
                if (result.Answers.TryGetValue(key, out TypedAnswer? answer) && answer != null && answer.Noul.HasValue)
                    value = answer.Noul.Value;

                // A missing or unanswered section contributes zero, which can never satisfy the accept
                // condition — the conservative reading.
                if (value < min) min = value;
                any = true;
            }

            return any && min != double.MaxValue ? min : 0.0;
        }

        private static string ScoreLabel(double score)
        {
            if (score < 0.0) return "unscored";
            int index = (int)Math.Round(score, MidpointRounding.AwayFromZero);
            if (index < 0) index = 0;
            if (index >= _ScoreLevels.Count) index = _ScoreLevels.Count - 1;
            return _ScoreLevels[index];
        }

        private static string Ordinal(int position)
        {
            return position switch
            {
                1 => "first",
                2 => "second",
                3 => "third",
                4 => "fourth",
                _ => position.ToString(CultureInfo.InvariantCulture) + "th"
            };
        }

        #endregion
    }
}
