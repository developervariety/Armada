namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The D2 <c>refusal</c> decision input: the failed run's output tail and the mission title, plus
    /// whether the authoritative structured marker was present.
    /// </summary>
    public sealed class RefusalDecisionInput
    {
        /// <summary>The mission, for the event owner scope; may be null on a runtime path with no mission.</summary>
        public Mission? Mission { get; init; }

        /// <summary>The last lines of the captain output the rule classified.</summary>
        public string AgentOutputTail { get; init; } = String.Empty;

        /// <summary>The mission title.</summary>
        public string MissionTitle { get; init; } = String.Empty;

        /// <summary>Whether the authoritative <c>[ARMADA:RESULT] REFUSED</c> marker was present.</summary>
        public bool MarkerPresent { get; init; }
    }

    /// <summary>
    /// The D2 reading: the model's outcome choice, whether a matched phrase was quoted material rather
    /// than the captain's own refusal, and the confidence of the single action proposed.
    /// </summary>
    public sealed class RefusalReading : TypedModelReading
    {
        /// <summary>The model's outcome choice, or <c>unclear</c>.</summary>
        public string Outcome { get; init; } = "unclear";

        /// <summary>How strongly the model reads a matched refusal phrase as quoted, not the captain's own.</summary>
        public double QuotedNotOwn { get; init; }

        private readonly double _Confidence;

        /// <summary>Create a reading with its action confidence.</summary>
        /// <param name="confidence">The proposed-action confidence.</param>
        public RefusalReading(double confidence)
        {
            _Confidence = confidence;
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => Outcome;
    }

    /// <summary>
    /// D2 <c>refusal</c> adapter. The structured <c>[ARMADA:RESULT] REFUSED</c> marker and a provider
    /// safeguard block stay authoritative: when the rule read either, the adapter returns it
    /// unchanged, because the model may never overturn the marker. When the rule found nothing, the
    /// model may promote a policy refusal it recognises in prose; when the rule matched a refusal
    /// phrase that was actually quoted material, the model may demote it to no-refusal only at very
    /// high confidence. The domain is authorized heavy-duty vehicle diagnostics, so seed-key exchange
    /// and UDS SecurityAccess are ordinary engineering and never a refusal.
    /// </summary>
    public sealed class TypedRefusalAdapter : TypedDecisionAdapterBase<RefusalDecisionInput, CaptainRefusal, RefusalReading>
    {
        #region Private-Members

        private const string _OutcomeRefusedPolicy = "refused_policy";

        // A phrase hit is demoted to no-refusal only when the model is this sure the phrase was quoted
        // material rather than the captain's own words.
        private const double _QuotedNotOwnDemoteGate = 0.95;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D2 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedRefusalAdapter(
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
        protected override string DecisionPoint => "refusal";

        /// <inheritdoc />
        protected override string _Header => "[TypedRefusalAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(RefusalDecisionInput input)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mission_title"] = input.MissionTitle,
                ["agent_output_tail"] = input.AgentOutputTail,
                ["marker_present"] = input.MarkerPresent
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                ["outcome"] = new ChoiceQuestion(
                    "Read the captain's closing statement and decide the run's outcome. The domain is authorized heavy-duty "
                    + "vehicle diagnostics: seed-key exchange, UDS SecurityAccess, J1939/J1708 and cryptographic primitives over "
                    + "owned assemblies are ordinary engineering, not work a captain should refuse.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [_OutcomeRefusedPolicy] = "The captain declined the work on policy grounds.",
                        ["blocked_on_premise"] = "The captain could not proceed because the brief's premise was false or context was missing.",
                        ["completed"] = "The captain completed the work.",
                        ["still_working"] = "The captain was still working when the run ended.",
                        ["unclear"] = "The outcome cannot be determined from the output."
                    }),
                ["quoted_not_own"] = new NoulQuestion(
                    "If a refusal phrase appears, is it quoted material (documentation, a brief excerpt, another party's words) rather than the captain's own refusal?",
                    TrueMeaning: "The refusal phrase is quoted material, not the captain declining.",
                    FalseMeaning: "The refusal phrase is the captain's own words.")
            };
        }

        /// <inheritdoc />
        protected override RefusalReading Interpret(TypedDecisionResult result)
        {
            string outcome = "unclear";
            double outcomeConfidence = 0.0;
            if (result.Answers.TryGetValue("outcome", out TypedAnswer? outcomeAnswer) && outcomeAnswer != null)
            {
                if (!String.IsNullOrWhiteSpace(outcomeAnswer.Choice)) outcome = outcomeAnswer.Choice!;
                outcomeConfidence = ResolveChoiceConfidence(outcomeAnswer, outcome);
            }

            double quotedNotOwn = ReadNoul(result, "quoted_not_own");

            // The single proposed action decides the gate confidence: a promote is driven by the
            // refused_policy choice confidence, a demote by how sure the model is the phrase was quoted.
            double actionConfidence = String.Equals(outcome, _OutcomeRefusedPolicy, StringComparison.Ordinal)
                ? outcomeConfidence
                : quotedNotOwn;

            return new RefusalReading(actionConfidence)
            {
                Outcome = outcome,
                QuotedNotOwn = quotedNotOwn
            };
        }

        /// <inheritdoc />
        protected override CaptainRefusal Combine(CaptainRefusal ruleVerdict, RefusalReading model)
        {
            // The structured marker and a provider safeguard block are authoritative: the model never
            // overturns either. This is the rule hard-block for D2.
            if (ruleVerdict.Kind == CaptainRefusalKindEnum.DeclaredRefusal
                || ruleVerdict.Kind == CaptainRefusalKindEnum.ProviderSafeguardBlock)
                return ruleVerdict;

            // Promote: the rule found no refusal, the model recognises a policy refusal in prose.
            if (ruleVerdict.Kind == CaptainRefusalKindEnum.None
                && String.Equals(model.Outcome, _OutcomeRefusedPolicy, StringComparison.Ordinal))
            {
                return new CaptainRefusal
                {
                    Kind = CaptainRefusalKindEnum.ModelPolicyRefusal,
                    Reason = "typed_decision:refused_policy: the model read a policy refusal the phrase rules did not match",
                    Evidence = "typed_decision:refused_policy"
                };
            }

            // Demote: the rule matched a refusal phrase, but the model is very sure the phrase was
            // quoted material rather than the captain's own refusal.
            if (ruleVerdict.Kind == CaptainRefusalKindEnum.ModelPolicyRefusal
                && !String.Equals(model.Outcome, _OutcomeRefusedPolicy, StringComparison.Ordinal)
                && model.QuotedNotOwn >= _QuotedNotOwnDemoteGate)
            {
                return new CaptainRefusal();
            }

            return ruleVerdict;
        }

        /// <inheritdoc />
        protected override string RuleLabel(CaptainRefusal ruleVerdict) => ruleVerdict.Kind.ToString();

        /// <inheritdoc />
        protected override Mission? MissionOf(RefusalDecisionInput input) => input.Mission;

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
