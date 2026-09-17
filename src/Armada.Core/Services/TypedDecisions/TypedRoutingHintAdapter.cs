namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The D16 <c>routing_hint</c> verdict: a hint that reorders the already-approved, already-eligible
    /// Routing V2 routes for one mission by the work's SHAPE rather than by static list position. It is
    /// a hint only. It never creates a route, never picks an unlisted account or model, never moves a
    /// running mission, never overrides Reserve or Exhausted handling, and never touches the
    /// reserved-persona path — every Routing V2 rule stays a hard constraint applied after the reorder.
    /// The rule verdict is <see cref="None"/>: the plain V2 list order.
    /// </summary>
    public readonly struct RoutingHint
    {
        /// <summary>Whether the hint applies a reorder at all. False leaves the plain V2 list order.</summary>
        public bool Applied { get; }

        /// <summary>The chosen work shape whose tagged routes are preferred, or null when none was chosen at threshold.</summary>
        public string? ChosenShape { get; }

        /// <summary>Whether a policy-tolerant route is preferred because the work is policy-sensitive.</summary>
        public bool PreferPolicyTolerant { get; }

        private RoutingHint(bool applied, string? chosenShape, bool preferPolicyTolerant)
        {
            Applied = applied;
            ChosenShape = chosenShape;
            PreferPolicyTolerant = preferPolicyTolerant;
        }

        /// <summary>The rule verdict: no hint, the plain V2 list order.</summary>
        /// <returns>An unapplied hint.</returns>
        public static RoutingHint None() => new RoutingHint(false, null, false);

        /// <summary>Build an applied hint from a chosen shape and a policy-tolerant preference.</summary>
        /// <param name="chosenShape">The chosen shape, or null.</param>
        /// <param name="preferPolicyTolerant">Whether to prefer a policy-tolerant route.</param>
        /// <returns>An applied hint.</returns>
        public static RoutingHint Apply(string? chosenShape, bool preferPolicyTolerant)
        {
            return new RoutingHint(true, chosenShape, preferPolicyTolerant);
        }
    }

    /// <summary>
    /// The D16 <c>routing_hint</c> decision input: the objective and mission text the work's shape is
    /// read from, plus the tags carried by the routes the persona could be placed on (for context, not
    /// for selection — the model never picks a route).
    /// </summary>
    public sealed class RoutingHintDecisionInput
    {
        /// <summary>The mission being routed, for the event owner scope.</summary>
        public required Mission Mission { get; init; }

        /// <summary>The objective title.</summary>
        public string ObjectiveTitle { get; init; } = String.Empty;

        /// <summary>The objective description.</summary>
        public string Description { get; init; } = String.Empty;

        /// <summary>The objective acceptance criteria.</summary>
        public string AcceptanceCriteria { get; init; } = String.Empty;

        /// <summary>The stage persona.</summary>
        public string Persona { get; init; } = String.Empty;

        /// <summary>The pipeline name.</summary>
        public string Pipeline { get; init; } = String.Empty;

        /// <summary>The vessel's public name.</summary>
        public string VesselName { get; init; } = String.Empty;

        /// <summary>The brief size in bytes.</summary>
        public int BriefByteSize { get; init; }

        /// <summary>The tags carried by the persona's configured routes, for context.</summary>
        public IReadOnlyList<string> EligibleRouteShapes { get; init; } = new List<string>();
    }

    /// <summary>
    /// The D16 reading. The model answers a <c>shape</c> Choice, a <c>policy_sensitive</c> Noul, and two
    /// context Nouls. Two independent hint drivers are measured against the decision threshold at
    /// interpret time (the shape choice) and against an absolute 0.9 floor (policy sensitivity), so the
    /// reading carries each driver's eligibility and reports the stronger contribution as the single
    /// gate confidence.
    /// </summary>
    public sealed class RoutingHintReading : TypedModelReading
    {
        /// <summary>The chosen work shape, or "none".</summary>
        public string ShapeChoice { get; init; } = "none";

        /// <summary>The confidence of the shape choice.</summary>
        public double ShapeConfidence { get; init; }

        /// <summary>The policy-sensitivity Noul.</summary>
        public double PolicySensitive { get; init; }

        /// <summary>Whether the shape choice (other than "none") is at or above the decision threshold.</summary>
        public bool ShapeEligible { get; init; }

        /// <summary>Whether the policy-sensitivity Noul is at or above the 0.9 floor.</summary>
        public bool PolicyTolerantPreferred { get; init; }

        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>Create a reading with its single gate confidence and a short label.</summary>
        /// <param name="confidence">The stronger of the two proposed-action confidences.</param>
        /// <param name="label">The short verdict label for the event message.</param>
        public RoutingHintReading(double confidence, string label)
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
    /// D16 <c>routing_hint</c> adapter. Owner decision 2026-09-16: it is NOT wired into the legacy
    /// model-tier selector; it belongs to Routing V2. It chooses among the routes V2 already approved
    /// and found eligible for a persona, so the work's shape — not the list position — decides which
    /// approved account and model list receives the work. The rule verdict is the plain V2 list order,
    /// which is also the fallback whenever the decision is Off, unavailable, or below threshold. The
    /// adapter never throws into the caller, and every hard V2 constraint is applied after the hint.
    /// </summary>
    public sealed class TypedRoutingHintAdapter : TypedDecisionAdapterBase<RoutingHintDecisionInput, RoutingHint, RoutingHintReading>
    {
        #region Private-Members

        private const string _NoneShape = "none";

        // A policy-sensitive Noul prefers a policy-tolerant route at or above this absolute floor
        // (owner: policy_sensitive >= 0.9), independent of the decision's own gate threshold.
        private const double _PolicySensitiveFloor = 0.9;

        private static readonly IReadOnlyDictionary<string, string> _ShapeCriteria = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["audit"] = "A read-only audit or census: reading and reporting, no behaviour change.",
            ["reasoning-heavy"] = "Work whose difficulty is analysis: a subtle fix, a design, a hard diagnosis.",
            ["mechanical"] = "A routine, well-specified change: a rename, a small port, a mechanical edit.",
            ["doc-only"] = "Documentation or narrative work only.",
            ["port-fidelity"] = "A source port whose correctness is byte and logic fidelity to a decompiled source.",
            ["none"] = "The work has no distinctive shape, or the shape is unclear."
        };

        // The adapter keeps its own settings reference (also passed to the base) so the reading can
        // resolve the decision's gate threshold at interpret time: the shape driver is measured against
        // it while the policy-sensitivity driver uses an absolute floor.
        private readonly TypedDecisionSettings _Settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D16 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedRoutingHintAdapter(
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
        protected override string DecisionPoint => "routing_hint";

        /// <inheritdoc />
        protected override string _Header => "[TypedRoutingHintAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(RoutingHintDecisionInput input)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = input.ObjectiveTitle,
                ["description"] = input.Description,
                ["acceptance_criteria"] = input.AcceptanceCriteria,
                ["persona"] = input.Persona,
                ["pipeline"] = input.Pipeline,
                ["vessel"] = input.VesselName,
                ["brief_bytes"] = input.BriefByteSize,
                ["eligible_route_shapes"] = new List<string>(input.EligibleRouteShapes)
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                ["shape"] = new ChoiceQuestion(
                    "What is the shape of this work? Choose the single best fit for routing it to an approved account and model. "
                    + "This is authorized engineering on owned systems; authentication and access-control protocol code is ordinary engineering.",
                    _ShapeCriteria),
                ["policy_sensitive"] = new NoulQuestion(
                    "The work touches authentication, access-control, or other authorized security-protocol content a "
                    + "safety-tuned runtime has refused before, so it should route to an approved alternate runtime that handles it.",
                    TrueMeaning: "The work should route to a policy-tolerant runtime.",
                    FalseMeaning: "The work needs no policy-tolerant runtime."),
                ["context_heavy"] = new NoulQuestion(
                    "The work needs a large amount of context to do well.",
                    TrueMeaning: "The work is context-heavy.",
                    FalseMeaning: "The work fits a small context."),
                ["telemetry_needed"] = new NoulQuestion(
                    "The work benefits from a runtime with rich telemetry.",
                    TrueMeaning: "Rich telemetry helps this work.",
                    FalseMeaning: "Telemetry is not important for this work.")
            };
        }

        /// <inheritdoc />
        protected override RoutingHintReading Interpret(TypedDecisionResult result)
        {
            double threshold = _Settings.For(DecisionPoint).GateThreshold;

            string shapeChoice = _NoneShape;
            double shapeConfidence = 0.0;
            if (result.Answers.TryGetValue("shape", out TypedAnswer? shapeAnswer) && shapeAnswer != null)
            {
                if (!String.IsNullOrWhiteSpace(shapeAnswer.Choice)) shapeChoice = shapeAnswer.Choice!;
                shapeConfidence = ResolveChoiceConfidence(shapeAnswer, shapeChoice);
            }

            double policySensitive = ReadNoul(result, "policy_sensitive");

            bool shapeIsReal = !String.Equals(shapeChoice, _NoneShape, StringComparison.OrdinalIgnoreCase);
            double shapeContribution = shapeIsReal ? shapeConfidence : 0.0;
            bool shapeEligible = shapeContribution >= threshold && threshold > 0.0;
            bool policyTolerantPreferred = policySensitive >= _PolicySensitiveFloor;

            double policyContribution = policyTolerantPreferred ? policySensitive : 0.0;
            double confidence = Math.Max(shapeContribution, policyContribution);
            string label = "shape=" + shapeChoice + (policyTolerantPreferred ? " policy_tolerant" : String.Empty);

            return new RoutingHintReading(confidence, label)
            {
                ShapeChoice = shapeChoice,
                ShapeConfidence = shapeConfidence,
                PolicySensitive = policySensitive,
                ShapeEligible = shapeEligible,
                PolicyTolerantPreferred = policyTolerantPreferred
            };
        }

        /// <inheritdoc />
        protected override RoutingHint Combine(RoutingHint ruleVerdict, RoutingHintReading model)
        {
            // A hint applies when a real shape cleared the decision threshold, or when the work is
            // policy-sensitive at the absolute floor. Neither creates or selects a route; the reorder
            // only prefers an already-eligible route, and every V2 hard constraint runs afterward.
            if (!model.ShapeEligible && !model.PolicyTolerantPreferred) return ruleVerdict;

            string? chosenShape = model.ShapeEligible ? model.ShapeChoice : null;
            return RoutingHint.Apply(chosenShape, model.PolicyTolerantPreferred);
        }

        /// <inheritdoc />
        protected override string RuleLabel(RoutingHint ruleVerdict) => ruleVerdict.Applied ? "hint" : "v2_default_order";

        /// <inheritdoc />
        protected override Mission? MissionOf(RoutingHintDecisionInput input) => input.Mission;

        #endregion

        #region Private-Methods

        private static double ResolveChoiceConfidence(TypedAnswer answer, string choice)
        {
            if (answer.Confidence.HasValue) return answer.Confidence.Value;
            if (answer.Probabilities != null && answer.Probabilities.TryGetValue(choice, out double probability)) return probability;
            return 0.0;
        }

        private static double ReadNoul(TypedDecisionResult result, string key)
        {
            if (result.Answers.TryGetValue(key, out TypedAnswer? answer) && answer != null && answer.Noul.HasValue)
                return answer.Noul.Value;
            return 0.0;
        }

        #endregion
    }
}
