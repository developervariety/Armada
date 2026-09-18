namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The declarative half of a built-in typed decision: its questions, the fields of its input to
    /// serialize into the state, and the decision-point name. It ships as an embedded default in source
    /// and an operator may reword it in settings through a <see cref="BuiltinDecisionDefinitionOverride"/>.
    /// It carries no verdict and no action — those are the deterministic rule and <c>Combine</c>, which
    /// stay in C# — so no value here can make a decision approve. The threshold and mode live on the
    /// decision's <see cref="TypedDecisionRuleSettings"/> row, not here, because they are already
    /// settings-overridable there.
    /// </summary>
    public sealed class BuiltinDecisionDefinition
    {
        /// <summary>The decision-point name; a row in the settings decisions map.</summary>
        public string DecisionPoint { get; init; } = String.Empty;

        /// <summary>The questions this decision asks, in order. Reuses the custom-decision question shape.</summary>
        public IReadOnlyList<CustomTypedQuestionSettings> Questions { get; init; } = new List<CustomTypedQuestionSettings>();

        /// <summary>
        /// Which named fields of the decision's own context to serialize into the state, in order. The
        /// adapter supplies the context; this whitelist selects and orders it. A structural field, never
        /// overridable in settings.
        /// </summary>
        public IReadOnlyList<string> StateFields { get; init; } = new List<string>();
    }

    /// <summary>
    /// The operator-overridable part of a built-in decision definition. By construction it carries only
    /// human-facing wording: it has no field for a question's id set, its kind, its finding direction, or
    /// the state-field whitelist, so no settings edit can add or remove a question, change what an answer
    /// means for the gate, or widen what the decision serializes. The resolver applies these overrides
    /// onto the embedded default by matching an existing question id; an override for an unknown id is
    /// ignored. This is the in-code guarantee that an owner-edited definition can never flip a decision
    /// into an approving direction.
    /// </summary>
    public sealed class BuiltinDecisionDefinitionOverride
    {
        /// <summary>Per-question wording overrides, matched to the embedded default by <see cref="BuiltinQuestionWording.Id"/>.</summary>
        public List<BuiltinQuestionWording> Questions
        {
            get => _Questions;
            set => _Questions = value ?? new List<BuiltinQuestionWording>();
        }

        private List<BuiltinQuestionWording> _Questions = new List<BuiltinQuestionWording>();

        /// <summary>Deep-copy this override, so a hot reload replaces values in place without sharing lists.</summary>
        /// <returns>An independent copy.</returns>
        public BuiltinDecisionDefinitionOverride Clone()
        {
            List<BuiltinQuestionWording> questions = new List<BuiltinQuestionWording>();
            foreach (BuiltinQuestionWording question in _Questions) questions.Add(question.Clone());
            return new BuiltinDecisionDefinitionOverride { Questions = questions };
        }
    }

    /// <summary>
    /// A reworded question for a built-in decision. Only the wording is settable: the instructions, the
    /// meanings of existing choice options, and the meanings of a Noul's poles. The id names which
    /// embedded question to reword and is never a way to add one.
    /// </summary>
    public sealed class BuiltinQuestionWording
    {
        /// <summary>The id of an existing embedded question to reword.</summary>
        public string Id { get; set; } = String.Empty;

        /// <summary>Replacement instructions, when set.</summary>
        public string? Instructions { get; set; }

        /// <summary>
        /// Replacement meanings for existing choice options, by option name. Only names already present in
        /// the embedded option set are applied; a new name is ignored, so the option set cannot grow.
        /// </summary>
        public Dictionary<string, string> Options
        {
            get => _Options;
            set => _Options = value ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        /// <summary>Replacement meaning of a Noul's true pole, when set. The pole itself stays the finding.</summary>
        public string? TrueMeaning { get; set; }

        /// <summary>Replacement meaning of a Noul's false pole, when set.</summary>
        public string? FalseMeaning { get; set; }

        private Dictionary<string, string> _Options = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Deep-copy this wording override.</summary>
        /// <returns>An independent copy.</returns>
        public BuiltinQuestionWording Clone()
        {
            return new BuiltinQuestionWording
            {
                Id = Id,
                Instructions = Instructions,
                Options = new Dictionary<string, string>(_Options, StringComparer.Ordinal),
                TrueMeaning = TrueMeaning,
                FalseMeaning = FalseMeaning
            };
        }
    }
}
