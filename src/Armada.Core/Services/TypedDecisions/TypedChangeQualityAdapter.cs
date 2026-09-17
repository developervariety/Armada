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
    /// The reading of a change_quality result: the model dimensions the model judges weak, and the single
    /// gate confidence (the strongest weakness). A model flag on a deterministically-backed dimension is
    /// still informational here; the rule's own hard-flag is what makes a dimension routable.
    /// </summary>
    public sealed class ChangeQualityReading : TypedModelReading
    {
        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>The model-judged weak dimensions (each informational, ShouldFix).</summary>
        public IReadOnlyList<ChangeQualityWeakness> ModelWeaknesses { get; }

        /// <summary>Create a reading.</summary>
        /// <param name="confidence">The strongest model weakness confidence.</param>
        /// <param name="modelWeaknesses">The model weaknesses at or above the concern floor.</param>
        /// <param name="label">The short verdict label.</param>
        public ChangeQualityReading(double confidence, IReadOnlyList<ChangeQualityWeakness> modelWeaknesses, string label)
        {
            _Confidence = confidence;
            ModelWeaknesses = modelWeaknesses ?? new List<ChangeQualityWeakness>();
            _Label = label ?? String.Empty;
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => _Label;
    }

    /// <summary>
    /// The change_quality decision (jev-review-inspired): a multi-dimension read of a focused diff over
    /// DRY, cognitive complexity, modularity, readability and maintainability. Two dimensions are backed
    /// by a deterministic rule and hard-flag without the model (complexity by a metric, core_rule by the
    /// Slop check, both in <see cref="ChangeQualityRules"/>); the rest are informational — the model may
    /// flag or propose them, but they never hard-gate. The verdict is a set of weak dimensions; the
    /// deterministic weaknesses are authoritative and the model may only ADD informational ones, never
    /// remove a rule's. It never lands, dispatches, fails a stage, or edits a record; a consumer decides
    /// what to do with the weaknesses (a captain reads them; the orchestrator routes the routable ones to
    /// a Triaged follow-up). Ships in Gate, fails closed to the deterministic weaknesses, records one
    /// event per consulted call, and never throws into the caller.
    /// </summary>
    public sealed class TypedChangeQualityAdapter : TypedDecisionAdapterBase<ChangeQualityInput, ChangeQualityVerdict, ChangeQualityReading>
    {
        #region Private-Members

        /// <summary>The model Noul at or above which a dimension is a model weakness.</summary>
        private const double _ConcernFloor = 0.5;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the change_quality adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedChangeQualityAdapter(
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
        protected override string DecisionPoint => "change_quality";

        /// <inheritdoc />
        protected override string _Header => "[TypedChangeQualityAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(ChangeQualityInput input)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["diff"] = input.UnifiedDiff ?? String.Empty
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            questions[ChangeQualityDimensions.Dry + "_weak"] = new NoulQuestion(
                "The change duplicates logic that already exists or repeats itself, rather than reusing or extracting a shared path.",
                TrueMeaning: "The change violates DRY.",
                FalseMeaning: "The change does not duplicate logic.");
            questions[ChangeQualityDimensions.CognitiveComplexity + "_weak"] = new NoulQuestion(
                "The change is more cognitively complex or bloated than the work requires: deep nesting, long methods, or convoluted control flow.",
                TrueMeaning: "The change is over-complex or bloated.",
                FalseMeaning: "The change is about as simple as the work allows.");
            questions[ChangeQualityDimensions.Modularity + "_weak"] = new NoulQuestion(
                "The change weakens module boundaries or cohesion: a type or method takes on unrelated responsibilities, or reaches across a boundary it should not.",
                TrueMeaning: "The change weakens modularity.",
                FalseMeaning: "The change respects module boundaries.");
            questions[ChangeQualityDimensions.Readability + "_weak"] = new NoulQuestion(
                "The change is hard to read: unclear names, missing intent, or dense code a later reader would struggle with.",
                TrueMeaning: "The change is hard to read.",
                FalseMeaning: "The change reads clearly.");
            questions[ChangeQualityDimensions.Maintainability + "_weak"] = new NoulQuestion(
                "The change will be hard to maintain or change safely later: hidden coupling, fragile assumptions, or missing seams.",
                TrueMeaning: "The change harms maintainability.",
                FalseMeaning: "The change is maintainable.");
            return questions;
        }

        /// <inheritdoc />
        protected override ChangeQualityReading Interpret(TypedDecisionResult result)
        {
            List<ChangeQualityWeakness> weaknesses = new List<ChangeQualityWeakness>();
            double strongest = 0.0;
            foreach (string dimension in ChangeQualityDimensions.ModelDimensions)
            {
                string key = dimension + "_weak";
                if (!result.Answers.ContainsKey(key)) continue;
                double weak = TypedAnswerReader.ReadNoul(result, key, 0.0);
                if (weak < _ConcernFloor) continue;
                if (weak > strongest) strongest = weak;
                weaknesses.Add(new ChangeQualityWeakness
                {
                    Dimension = dimension,
                    Severity = ChangeQualitySeverity.ShouldFix,
                    Source = ChangeQualitySource.Model,
                    Reason = "the model reads " + dimension.Replace('_', ' ') + " as weak (confidence "
                        + weak.ToString("0.00", CultureInfo.InvariantCulture) + ")"
                });
            }

            string label = weaknesses.Count == 0
                ? "no_model_weakness"
                : "model_weak:" + weaknesses.Count.ToString(CultureInfo.InvariantCulture);
            return new ChangeQualityReading(strongest, weaknesses, label);
        }

        /// <inheritdoc />
        protected override ChangeQualityVerdict Combine(ChangeQualityVerdict ruleVerdict, ChangeQualityReading model)
        {
            // Combine runs only at a gate at or above threshold. The deterministic weaknesses are kept in
            // full; the model's informational weaknesses are added for any dimension the rule did not
            // already flag, so the model only ever ADDS a signal and never removes a rule's.
            List<ChangeQualityWeakness> merged = new List<ChangeQualityWeakness>(ruleVerdict.Weaknesses);
            HashSet<string> already = new HashSet<string>(StringComparer.Ordinal);
            foreach (ChangeQualityWeakness w in ruleVerdict.Weaknesses) already.Add(w.Dimension);
            foreach (ChangeQualityWeakness w in model.ModelWeaknesses)
            {
                if (already.Add(w.Dimension)) merged.Add(w);
            }

            return ChangeQualityVerdict.From(merged);
        }

        /// <inheritdoc />
        protected override string RuleLabel(ChangeQualityVerdict ruleVerdict) => ruleVerdict.OutcomeLabel;

        /// <inheritdoc />
        protected override Mission? MissionOf(ChangeQualityInput input) => input.Mission;

        #endregion
    }
}
