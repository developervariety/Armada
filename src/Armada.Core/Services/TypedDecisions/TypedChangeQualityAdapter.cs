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
            questions["dry_duplicates"] = new NoulQuestion(
                "The change duplicates logic that already exists, rather than reusing or extracting a shared path.",
                TrueMeaning: "The change duplicates existing logic.",
                FalseMeaning: "The change does not duplicate existing logic.");
            questions["complexity_nested"] = new NoulQuestion(
                "The change adds deep nesting beyond what the work requires.",
                TrueMeaning: "The change is over-nested.",
                FalseMeaning: "The nesting is about as simple as the work allows.");
            questions["complexity_long_method"] = new NoulQuestion(
                "The change adds a long method beyond what the work requires.",
                TrueMeaning: "The change adds a bloated method.",
                FalseMeaning: "Method length is about as simple as the work allows.");
            questions["complexity_convoluted"] = new NoulQuestion(
                "The change adds convoluted control flow beyond what the work requires.",
                TrueMeaning: "The change adds convoluted control flow.",
                FalseMeaning: "Control flow is about as simple as the work allows.");
            questions["modularity_unrelated"] = new NoulQuestion(
                "The change gives a type or method unrelated responsibilities.",
                TrueMeaning: "The change mixes unrelated responsibilities.",
                FalseMeaning: "The change keeps responsibilities together.");
            questions["modularity_crosses_boundary"] = new NoulQuestion(
                "The change reaches across a module boundary it should not.",
                TrueMeaning: "The change crosses a module boundary it should not.",
                FalseMeaning: "The change respects module boundaries.");
            questions["readability_unclear_names"] = new NoulQuestion(
                "The change uses names that hide intent.",
                TrueMeaning: "Names in the change hide intent.",
                FalseMeaning: "Names in the change read clearly.");
            questions["readability_missing_intent"] = new NoulQuestion(
                "The change is missing the intent a later reader needs.",
                TrueMeaning: "The change hides its intent.",
                FalseMeaning: "The change states its intent.");
            questions["readability_dense"] = new NoulQuestion(
                "The change is dense code a later reader would struggle with.",
                TrueMeaning: "The change is hard to read because it is dense.",
                FalseMeaning: "The change is not dense.");
            questions["maintainability_coupling"] = new NoulQuestion(
                "The change adds hidden coupling that will be hard to change later.",
                TrueMeaning: "The change adds hidden coupling.",
                FalseMeaning: "The change does not add hidden coupling.");
            questions["maintainability_assumptions"] = new NoulQuestion(
                "The change adds fragile assumptions that will be hard to change later.",
                TrueMeaning: "The change adds fragile assumptions.",
                FalseMeaning: "The change does not add fragile assumptions.");
            questions["maintainability_seams"] = new NoulQuestion(
                "The change is missing seams that would let a later change land safely.",
                TrueMeaning: "The change is missing maintainability seams.",
                FalseMeaning: "The change has the seams a later change needs.");
            return questions;
        }

        /// <inheritdoc />
        protected override ChangeQualityReading Interpret(TypedDecisionResult result)
        {
            List<ChangeQualityWeakness> weaknesses = new List<ChangeQualityWeakness>();
            double strongest = 0.0;
            foreach (string dimension in ChangeQualityDimensions.ModelDimensions)
            {
                double weak = WeakNoulFrom(result, dimension);
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

        #region Private-Methods

        private static readonly IReadOnlyDictionary<string, string[]> _DimensionAtoms =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [ChangeQualityDimensions.Dry] = new[] { "dry_duplicates" },
                [ChangeQualityDimensions.CognitiveComplexity] = new[] { "complexity_nested", "complexity_long_method", "complexity_convoluted" },
                [ChangeQualityDimensions.Modularity] = new[] { "modularity_unrelated", "modularity_crosses_boundary" },
                [ChangeQualityDimensions.Readability] = new[] { "readability_unclear_names", "readability_missing_intent", "readability_dense" },
                [ChangeQualityDimensions.Maintainability] = new[] { "maintainability_coupling", "maintainability_assumptions", "maintainability_seams" }
            };

        private static double WeakNoulFrom(TypedDecisionResult result, string dimension)
        {
            if (!_DimensionAtoms.TryGetValue(dimension, out string[]? atoms) || atoms == null || atoms.Length == 0)
                return TypedAnswerReader.ReadNoul(result, dimension + "_weak", 0.0);

            bool hasSplit = false;
            double max = 0.0;
            foreach (string atom in atoms)
            {
                if (result.Answers == null || !result.Answers.ContainsKey(atom)) continue;
                hasSplit = true;
                double value = TypedAnswerReader.ReadNoul(result, atom, 0.0);
                if (value > max) max = value;
            }
            if (hasSplit) return max;
            return TypedAnswerReader.ReadNoul(result, dimension + "_weak", 0.0);
        }

        #endregion
    }
}
