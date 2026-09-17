namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// One resolved pipeline stage as the D19 <c>stage_necessity</c> decision sees it. The rule
    /// verdict carries every stage in pipeline order with its persona and whether it is the Judge;
    /// the model may only ever ADD a skip proposal to a non-Judge stage, so a stage the rule retained
    /// stays retained unless the model proposes otherwise above threshold. The Judge is never a skip
    /// candidate and never carries a model flag.
    /// </summary>
    public sealed class StageNecessityStageResult
    {
        /// <summary>The stage persona name (for example <c>Worker</c>, <c>TestEngineer</c>, <c>Judge</c>).</summary>
        public required string PersonaName { get; init; }

        /// <summary>The stage execution order within the pipeline (1-based).</summary>
        public int Order { get; init; }

        /// <summary>Whether this stage is the Judge. A Judge stage is never a skip candidate.</summary>
        public bool IsJudge { get; init; }

        /// <summary>
        /// Whether the model proposes this stage as OPTIONAL: a skip the operator confirms before the
        /// voyage is materialised. Only ever set on a non-Judge stage at or above the decision
        /// threshold. Never set below the threshold and never set for the Judge.
        /// </summary>
        public bool ModelOptional { get; init; }

        /// <summary>
        /// Whether the model proposes this stage for AUTO-skip: a skip taken without an operator
        /// confirmation. Only ever set on a non-Judge stage at or above <c>0.95</c>, and it always
        /// implies <see cref="ModelOptional"/>. Never set for the Judge and never below <c>0.95</c>.
        /// </summary>
        public bool ModelAutoSkip { get; init; }

        /// <summary>The model's skip-reason choice for this stage, or <c>none</c>.</summary>
        public string SkipReason { get; init; } = "none";

        /// <summary>
        /// The model's confidence that this stage may be skipped, in [0, 1]: one minus the model's
        /// probability that the stage adds value. Zero for a retained stage and for the Judge.
        /// </summary>
        public double SkipConfidence { get; init; }
    }

    /// <summary>
    /// The D19 <c>stage_necessity</c> verdict: the resolved pipeline stages, each carrying the rule's
    /// retain decision and any model-proposed skip. The rule verdict retains every stage; a gated
    /// verdict may add an <see cref="StageNecessityStageResult.ModelOptional"/> or
    /// <see cref="StageNecessityStageResult.ModelAutoSkip"/> flag to a non-Judge stage only. The
    /// deterministic path always wins: the model can propose a skip but never REMOVE a stage the rule
    /// keeps below the auto-skip bar, and never touches the Judge.
    /// </summary>
    public sealed class StageNecessityVerdict
    {
        /// <summary>The resolved stages in pipeline order.</summary>
        public IReadOnlyList<StageNecessityStageResult> Stages { get; }

        /// <summary>A short label for the effective outcome: <c>rule</c> or <c>gated</c>.</summary>
        public string Outcome { get; }

        private StageNecessityVerdict(IReadOnlyList<StageNecessityStageResult> stages, string outcome)
        {
            Stages = stages ?? new List<StageNecessityStageResult>();
            Outcome = outcome ?? "rule";
        }

        /// <summary>The non-Judge stages the model proposes as optional (operator-confirmable skips).</summary>
        public IReadOnlyList<StageNecessityStageResult> OptionalStages =>
            Stages.Where(stage => stage.ModelOptional && !stage.IsJudge).ToList();

        /// <summary>The non-Judge stages the model proposes for auto-skip (at or above 0.95).</summary>
        public IReadOnlyList<StageNecessityStageResult> AutoSkipStages =>
            Stages.Where(stage => stage.ModelAutoSkip && !stage.IsJudge).ToList();

        /// <summary>Build the rule verdict from the resolved stages: every stage retained, no model flag.</summary>
        /// <param name="stages">The resolved stages in pipeline order.</param>
        /// <returns>The rule verdict.</returns>
        public static StageNecessityVerdict Rule(IReadOnlyList<StageNecessityStageResult> stages)
        {
            return new StageNecessityVerdict(stages, "rule");
        }

        /// <summary>Build a gated verdict from the combined stage results.</summary>
        /// <param name="stages">The stages with any model flags applied.</param>
        /// <returns>The gated verdict.</returns>
        public static StageNecessityVerdict Gated(IReadOnlyList<StageNecessityStageResult> stages)
        {
            return new StageNecessityVerdict(stages, "gated");
        }
    }

    /// <summary>
    /// One candidate stage the model is asked about: a non-Judge stage in pipeline order.
    /// </summary>
    public sealed class StageNecessityCandidate
    {
        /// <summary>The candidate stage persona name.</summary>
        public required string PersonaName { get; init; }

        /// <summary>The candidate stage order within the pipeline.</summary>
        public int Order { get; init; }

        /// <summary>The stage description from its persona template, when known.</summary>
        public string? Description { get; init; }
    }

    /// <summary>
    /// The D19 <c>stage_necessity</c> decision input: the objective, the resolved pipeline stages, and
    /// the prior-stage diff information the handoff seam supplies (empty at preview time). The model is
    /// asked only about the NON-Judge stages; the Judge is always retained.
    /// </summary>
    public sealed class StageNecessityDecisionInput
    {
        /// <summary>The mission this decision belongs to, when re-asked at a stage handoff; null at preview time.</summary>
        public Mission? Mission { get; init; }

        /// <summary>The objective title.</summary>
        public string Title { get; init; } = String.Empty;

        /// <summary>The objective description.</summary>
        public string Description { get; init; } = String.Empty;

        /// <summary>The objective acceptance criteria, in order.</summary>
        public IReadOnlyList<string> AcceptanceCriteria { get; init; } = new List<string>();

        /// <summary>The objective kind (Chore, Research, and so on).</summary>
        public string Kind { get; init; } = String.Empty;

        /// <summary>The resolved pipeline stages in order, each with persona, order, and Judge flag.</summary>
        public IReadOnlyList<StageNecessityStageResult> Stages { get; init; } = new List<StageNecessityStageResult>();

        /// <summary>Per-stage descriptions keyed by persona name, from the persona templates, when known.</summary>
        public IReadOnlyDictionary<string, string> StageDescriptions { get; init; } = new Dictionary<string, string>();

        /// <summary>A compact stat of the prior stages' diff, when re-asked at a handoff; empty at preview time.</summary>
        public string PriorDiffStat { get; init; } = String.Empty;

        /// <summary>The files the prior stages touched, when re-asked at a handoff; empty at preview time.</summary>
        public IReadOnlyList<string> FilesTouched { get; init; } = new List<string>();
    }

    /// <summary>
    /// The D19 reading. The model answers, per non-Judge candidate stage, one <c>adds_value</c> Noul
    /// and one <c>skip_reason</c> Choice. The single gate confidence is the greatest skip confidence
    /// over the answered candidate slots (one minus the smallest <c>adds_value</c>), so the generic
    /// gate entering <c>Combine</c> means at least one non-Judge stage is a skip candidate at
    /// threshold. The per-slot detail drives which stages are marked, and is consulted only in
    /// <c>Combine</c>.
    /// </summary>
    public sealed class StageNecessityReading : TypedModelReading
    {
        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>Per-slot skip confidence (one minus adds_value), keyed by 1-based candidate slot.</summary>
        public IReadOnlyDictionary<int, double> SlotSkipConfidence { get; }

        /// <summary>Per-slot skip reason, keyed by 1-based candidate slot.</summary>
        public IReadOnlyDictionary<int, string> SlotSkipReason { get; }

        /// <summary>Create a reading.</summary>
        /// <param name="slotSkipConfidence">Per-slot skip confidence.</param>
        /// <param name="slotSkipReason">Per-slot skip reason.</param>
        /// <param name="confidence">The single gate confidence.</param>
        /// <param name="label">The short verdict label for the event message.</param>
        public StageNecessityReading(
            IReadOnlyDictionary<int, double> slotSkipConfidence,
            IReadOnlyDictionary<int, string> slotSkipReason,
            double confidence,
            string label)
        {
            SlotSkipConfidence = slotSkipConfidence ?? new Dictionary<int, double>();
            SlotSkipReason = slotSkipReason ?? new Dictionary<int, string>();
            _Confidence = confidence;
            _Label = label ?? String.Empty;
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => _Label;
    }

    /// <summary>
    /// D19 <c>stage_necessity</c> adapter. Every pipeline runs every persona; a stage the objective
    /// does not need is a full captain run for nothing. This adapter sits on the resolved pipeline
    /// stages (in the dispatch preview, and re-asked at each stage handoff with the finished stage's
    /// output) and lets the model propose which NON-Judge stages could be skipped for this objective.
    /// It is additive and conservative:
    /// <list type="bullet">
    /// <item>The model is never asked about the Judge, and <c>Combine</c> never marks a Judge stage,
    /// so the Judge is never proposed for a skip.</item>
    /// <item>Below the decision threshold a stage is retained; at or above it the stage is proposed as
    /// OPTIONAL — a skip the operator confirms in the preview before the voyage is materialised.</item>
    /// <item>Only at or above <c>0.95</c> may a non-Judge stage be proposed for AUTO-skip. The model
    /// never removes a stage by itself below <c>0.95</c>.</item>
    /// </list>
    /// The deterministic rule (which stages the pipeline resolves) is the fallback and always wins: the
    /// model proposes, it never removes. The adapter never throws into the caller.
    /// </summary>
    public sealed class TypedStageNecessityAdapter : TypedDecisionAdapterBase<StageNecessityDecisionInput, StageNecessityVerdict, StageNecessityReading>
    {
        #region Private-Members

        // The judge persona name. A stage with this persona is never a skip candidate.
        private const string _JudgePersona = "Judge";

        // The auto-skip bar. Below it the model may only propose a stage as optional (operator
        // confirms); at or above it the stage may be auto-skipped. Never applies to the Judge.
        private const double _AutoSkipConfidence = 0.95;

        // The maximum number of non-Judge candidate stages the model is asked about. A pipeline with
        // more non-Judge stages retains the excess (they are never a skip candidate), the conservative
        // reading.
        private const int _MaxCandidates = 8;

        private static readonly IReadOnlyList<string> _SkipReasons = new List<string>
        {
            "none",
            "docs_only",
            "no_tests_in_scope",
            "no_ui",
            "trivial_change",
            "report_only",
            "nothing_to_record"
        };

        // The adapter keeps its own settings reference so Combine can read the effective gate
        // threshold: the base gate decides only whether to enter Combine, but the per-stage optional
        // decision compares each stage's own skip confidence against the same threshold.
        private readonly TypedDecisionSettings _Settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D19 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedStageNecessityAdapter(
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            TypedDecisionSettings settings,
            LoggingModule logging)
            : base(client, recorder, settings, logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion

        #region Protected-Overrides

        /// <inheritdoc />
        protected override string DecisionPoint => "stage_necessity";

        /// <inheritdoc />
        protected override string _Header => "[TypedStageNecessityAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(StageNecessityDecisionInput input)
        {
            List<StageNecessityStageResult> candidates = Candidates(input.Stages);
            List<object> stageStates = new List<object>();
            for (int i = 0; i < candidates.Count; i++)
            {
                StageNecessityStageResult stage = candidates[i];
                string? description = null;
                if (input.StageDescriptions != null)
                    input.StageDescriptions.TryGetValue(stage.PersonaName, out description);

                stageStates.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["slot"] = i + 1,
                    ["persona"] = stage.PersonaName,
                    ["order"] = stage.Order,
                    ["description"] = description
                });
            }

            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = input.Title,
                ["description"] = input.Description,
                ["acceptance_criteria"] = new List<string>(input.AcceptanceCriteria),
                ["kind"] = input.Kind,
                ["candidate_stages"] = stageStates,
                ["prior_diff_stat"] = input.PriorDiffStat,
                ["files_touched"] = new List<string>(input.FilesTouched)
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            for (int i = 1; i <= _MaxCandidates; i++)
            {
                string slot = i.ToString(CultureInfo.InvariantCulture);
                questions["stage_" + slot + "_adds_value"] = new NoulQuestion(
                    "Candidate stage number " + slot + " (see candidate_stages in the state, in order) ADDS VALUE for THIS objective: "
                    + "its persona would change the outcome, not merely run for form. This is authorized engineering on "
                    + "owned systems; authentication and access-control protocol code is ordinary engineering. "
                    + "If the state lists fewer than " + _MaxCandidates + " candidate stages and this slot has none, answer at the TRUE pole.",
                    TrueMeaning: "The stage adds value and must run.",
                    FalseMeaning: "The stage would not change the outcome for this objective.");

                questions["stage_" + slot + "_skip_reason"] = new ChoiceQuestion(
                    "If candidate stage number " + slot + " does not add value, why? Choose the closest reason, or none when it does add value.",
                    _SkipReasons.ToDictionary(reason => reason, reason => reason, StringComparer.Ordinal));
            }

            return questions;
        }

        /// <inheritdoc />
        protected override StageNecessityReading Interpret(TypedDecisionResult result)
        {
            Dictionary<int, double> slotSkipConfidence = new Dictionary<int, double>();
            Dictionary<int, string> slotSkipReason = new Dictionary<int, string>();
            double maxSkip = 0.0;

            for (int i = 1; i <= _MaxCandidates; i++)
            {
                string slot = i.ToString(CultureInfo.InvariantCulture);

                // A missing adds_value answer defaults to the TRUE pole (adds value), so the stage's
                // skip confidence is zero and it is never a skip candidate — the conservative reading.
                double addsValue = 1.0;
                if (result.Answers.TryGetValue("stage_" + slot + "_adds_value", out TypedAnswer? answer)
                    && answer != null && answer.Noul.HasValue)
                {
                    addsValue = answer.Noul.Value;
                }

                double skipConfidence = 1.0 - addsValue;
                if (skipConfidence < 0.0) skipConfidence = 0.0;
                if (skipConfidence > 1.0) skipConfidence = 1.0;
                slotSkipConfidence[i] = skipConfidence;
                if (skipConfidence > maxSkip) maxSkip = skipConfidence;

                string reason = "none";
                if (result.Answers.TryGetValue("stage_" + slot + "_skip_reason", out TypedAnswer? reasonAnswer)
                    && reasonAnswer != null && !String.IsNullOrWhiteSpace(reasonAnswer.Choice))
                {
                    reason = reasonAnswer.Choice!.Trim();
                }
                slotSkipReason[i] = reason;
            }

            return new StageNecessityReading(slotSkipConfidence, slotSkipReason, maxSkip, LabelFor(slotSkipConfidence));
        }

        /// <inheritdoc />
        protected override StageNecessityVerdict Combine(StageNecessityVerdict ruleVerdict, StageNecessityReading model)
        {
            double threshold = _Settings.For(DecisionPoint).GateThreshold;
            List<StageNecessityStageResult> candidates = Candidates(ruleVerdict.Stages);

            // Map each candidate slot back to its non-Judge stage and apply the model's proposal. A
            // Judge stage is never in the candidate set, so it can never be marked here.
            Dictionary<int, StageNecessityStageResult> combinedByOrder = new Dictionary<int, StageNecessityStageResult>();
            for (int i = 0; i < candidates.Count; i++)
            {
                int slot = i + 1;
                StageNecessityStageResult candidate = candidates[i];
                double skipConfidence = model.SlotSkipConfidence.TryGetValue(slot, out double value) ? value : 0.0;
                string reason = model.SlotSkipReason.TryGetValue(slot, out string? r) ? (r ?? "none") : "none";

                bool autoSkip = !candidate.IsJudge && skipConfidence >= _AutoSkipConfidence;
                bool optional = !candidate.IsJudge && skipConfidence >= threshold;

                combinedByOrder[candidate.Order] = new StageNecessityStageResult
                {
                    PersonaName = candidate.PersonaName,
                    Order = candidate.Order,
                    IsJudge = candidate.IsJudge,
                    ModelOptional = optional,
                    ModelAutoSkip = autoSkip,
                    SkipReason = optional ? reason : "none",
                    SkipConfidence = skipConfidence
                };
            }

            List<StageNecessityStageResult> combined = new List<StageNecessityStageResult>();
            foreach (StageNecessityStageResult stage in ruleVerdict.Stages)
            {
                combined.Add(combinedByOrder.TryGetValue(stage.Order, out StageNecessityStageResult? applied) && applied != null
                    ? applied
                    : stage);
            }

            return StageNecessityVerdict.Gated(combined);
        }

        /// <inheritdoc />
        protected override string RuleLabel(StageNecessityVerdict ruleVerdict)
        {
            return "stages=" + ruleVerdict.Stages.Count.ToString(CultureInfo.InvariantCulture);
        }

        /// <inheritdoc />
        protected override Mission? MissionOf(StageNecessityDecisionInput input) => input.Mission;

        #endregion

        #region Private-Methods

        private static List<StageNecessityStageResult> Candidates(IReadOnlyList<StageNecessityStageResult> stages)
        {
            return (stages ?? new List<StageNecessityStageResult>())
                .Where(stage => stage != null && !stage.IsJudge)
                .OrderBy(stage => stage.Order)
                .Take(_MaxCandidates)
                .ToList();
        }

        private static bool IsJudgePersona(string? persona)
        {
            return String.Equals(persona, _JudgePersona, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Build a stage result from a pipeline persona; the Judge flag is derived from the persona name.</summary>
        /// <param name="order">The stage order.</param>
        /// <param name="persona">The stage persona name.</param>
        /// <returns>A retained (rule) stage result.</returns>
        public static StageNecessityStageResult Stage(int order, string persona)
        {
            return new StageNecessityStageResult
            {
                PersonaName = persona ?? String.Empty,
                Order = order,
                IsJudge = IsJudgePersona(persona)
            };
        }

        private static string LabelFor(IReadOnlyDictionary<int, double> slotSkipConfidence)
        {
            int candidates = slotSkipConfidence.Count(pair => pair.Value > 0.0);
            return "skip_candidates=" + candidates.ToString(CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
