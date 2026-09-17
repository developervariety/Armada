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
    /// The <c>capacity_escalation</c> decision input: the persona, the work text, and the persona's model lists.
    /// </summary>
    public sealed class CapacityEscalationDecisionInput
    {
        /// <summary>The mission being routed, for the event owner scope.</summary>
        public required Mission Mission { get; init; }

        /// <summary>The mission persona.</summary>
        public string Persona { get; init; } = String.Empty;

        /// <summary>The objective or mission title.</summary>
        public string Title { get; init; } = String.Empty;

        /// <summary>The objective or mission description; the adapter sends only its head.</summary>
        public string Description { get; init; } = String.Empty;

        /// <summary>The persona's default models.</summary>
        public IReadOnlyList<string> DefaultModels { get; init; } = new List<string>();

        /// <summary>The persona's lighter models.</summary>
        public IReadOnlyList<string> LighterModels { get; init; } = new List<string>();

        /// <summary>The persona's stronger models.</summary>
        public IReadOnlyList<string> StrongerModels { get; init; } = new List<string>();
    }

    /// <summary>
    /// The <c>capacity_escalation</c> reading: the chosen model list, or null when the answer named no option.
    /// </summary>
    public sealed class CapacityEscalationReading : TypedModelReading
    {
        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>Create a reading.</summary>
        /// <param name="choice">The chosen list, or null when the answer named no option.</param>
        /// <param name="confidence">The confidence of the choice; zero when no option was named.</param>
        public CapacityEscalationReading(CapacityChoiceEnum? choice, double confidence)
        {
            Choice = choice;
            _Confidence = choice.HasValue ? confidence : 0.0;
            _Label = choice.HasValue ? TypedCapacityEscalationAdapter.OptionName(choice.Value) : "no_option";
        }

        /// <summary>The chosen model list, or null.</summary>
        public CapacityChoiceEnum? Choice { get; }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => _Label;
    }

    /// <summary>
    /// <c>capacity_escalation</c> adapter. At assignment, for a persona whose Smart Routing model preference has
    /// a Lighter or Stronger list, it asks one closed choice: is the work routine (lighter), a fit for the
    /// default models (default), or harder than the default is expected to handle (stronger). The rule verdict
    /// is Default, which is also the result whenever the decision is Off, unavailable, or below threshold. The
    /// reading only orders the persona's model groups; it never makes a captain eligible, approves, lands,
    /// dispatches, or edits a record.
    /// </summary>
    public sealed class TypedCapacityEscalationAdapter : TypedDecisionAdapterBase<CapacityEscalationDecisionInput, CapacityChoiceEnum, CapacityEscalationReading>
    {
        #region Public-Members

        /// <summary>The decision name: its settings key and event area.</summary>
        public const string DecisionName = "capacity_escalation";

        /// <summary>The question id.</summary>
        public const string QuestionId = "capacity";

        #endregion

        #region Private-Members

        private const int _DescriptionHeadChars = 2000;

        private static readonly IReadOnlyDictionary<string, string> _Criteria = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lighter"] = "Routine, well-specified, mechanical work: a rename, a small edit, a documented step with no open design question.",
            ["default"] = "Ordinary work that fits the persona's default model.",
            ["stronger"] = "Work harder than the default model is expected to handle: a subtle fix, a cross-repository design, or a hard diagnosis."
        };

        private readonly TypedDecisionSettings _Settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedCapacityEscalationAdapter(
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            TypedDecisionSettings settings,
            LoggingModule logging)
            : base(client, recorder, settings, logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion

        #region Public-Methods

        /// <summary>The option name the question uses for a choice.</summary>
        /// <param name="choice">The choice.</param>
        /// <returns><c>lighter</c>, <c>default</c>, or <c>stronger</c>.</returns>
        public static string OptionName(CapacityChoiceEnum choice)
        {
            return choice == CapacityChoiceEnum.Lighter ? "lighter" : choice == CapacityChoiceEnum.Stronger ? "stronger" : "default";
        }

        #endregion

        #region Protected-Overrides

        /// <inheritdoc />
        protected override string DecisionPoint => DecisionName;

        /// <inheritdoc />
        protected override string _Header => "[TypedCapacityEscalationAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(CapacityEscalationDecisionInput input)
        {
            int headChars = Math.Min(_DescriptionHeadChars, _Settings.MaxStateChars / 2);
            string description = input.Description ?? String.Empty;
            if (description.Length > headChars) description = description.Substring(0, headChars);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["persona"] = input.Persona,
                ["title"] = input.Title,
                ["description_head"] = description,
                ["default_models"] = new List<string>(input.DefaultModels),
                ["lighter_models"] = new List<string>(input.LighterModels),
                ["stronger_models"] = new List<string>(input.StrongerModels)
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                [QuestionId] = new ChoiceQuestion(
                    "Which model capacity does this work need? Choose lighter for routine work, default when the persona's default model fits, "
                    + "and stronger only when the work is harder than the default model is expected to handle.",
                    _Criteria)
            };
        }

        /// <inheritdoc />
        protected override CapacityEscalationReading Interpret(TypedDecisionResult result)
        {
            if (!result.Answers.TryGetValue(QuestionId, out TypedAnswer? answer) || answer == null || String.IsNullOrWhiteSpace(answer.Choice))
                return new CapacityEscalationReading(null, 0.0);
            string option = answer.Choice!.Trim().ToLowerInvariant();
            CapacityChoiceEnum? choice = option == "lighter" ? CapacityChoiceEnum.Lighter
                : option == "stronger" ? CapacityChoiceEnum.Stronger
                : option == "default" ? CapacityChoiceEnum.Default
                : null;
            double confidence = answer.Confidence
                ?? (answer.Probabilities != null && answer.Probabilities.TryGetValue(answer.Choice!, out double probability) ? probability : 0.0);
            return new CapacityEscalationReading(choice, confidence);
        }

        /// <inheritdoc />
        protected override CapacityChoiceEnum Combine(CapacityChoiceEnum ruleVerdict, CapacityEscalationReading model)
        {
            return model.Choice ?? ruleVerdict;
        }

        /// <inheritdoc />
        protected override string RuleLabel(CapacityChoiceEnum ruleVerdict) => OptionName(ruleVerdict);

        /// <inheritdoc />
        protected override Mission? MissionOf(CapacityEscalationDecisionInput input) => input.Mission;

        #endregion
    }
}
