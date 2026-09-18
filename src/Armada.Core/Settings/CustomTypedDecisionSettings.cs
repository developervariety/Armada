namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// A user-defined custom typed decision. Operators create and edit these from the dashboard or
    /// the settings API; they are stored in the <c>typedDecisions.custom</c> settings map and
    /// hot-reload in place. A custom decision carries its own questions and describes how to build
    /// its state, but it never wires itself into orchestration code: it runs only at a generic
    /// <see cref="Surface"/>, and its <see cref="Binding"/> is limited to the fixed, non-approving
    /// action list in <see cref="CustomDecisionSeamEnum"/>. It can flag, annotate, or record; it can
    /// never land, dispatch, approve a PASS, or write memory.
    /// </summary>
    public class CustomTypedDecisionSettings
    {
        /// <summary>
        /// The decision's own mode, capped by the global typed-decision mode. Default Off, so a newly
        /// created decision records nothing until the operator turns it on.
        /// </summary>
        public TypedDecisionModeEnum Mode { get; set; } = TypedDecisionModeEnum.Off;

        /// <summary>
        /// Confidence at or above which the decision's binding action is taken. Below it the decision
        /// only records. Clamped to [0, 1]; zero means unset.
        /// </summary>
        public double GateThreshold
        {
            get => _GateThreshold;
            set => _GateThreshold = Math.Max(0.0, Math.Min(1.0, value));
        }

        /// <summary>Whether this decision's redacted state is retained on the host as training data.</summary>
        public bool RetainState { get; set; } = false;

        /// <summary>One-line description shown in the dashboard and the status API.</summary>
        public string Description { get; set; } = String.Empty;

        /// <summary>Where the decision runs. Default the on-demand captain/operator tool.</summary>
        public CustomDecisionSurfaceEnum Surface { get; set; } = CustomDecisionSurfaceEnum.CaptainTool;

        /// <summary>
        /// The conservative action taken when the decision gates at or above the threshold. Default
        /// None (record only). Every option is non-approving by construction.
        /// </summary>
        public CustomDecisionSeamEnum Binding { get; set; } = CustomDecisionSeamEnum.None;

        /// <summary>
        /// For the MissionDiff surface, which mission fields to assemble into the state, in order.
        /// Recognised names: <c>title</c>, <c>persona</c>, <c>diff</c>, <c>output_tail</c>,
        /// <c>changed_paths</c>, <c>failure_reason</c>. Unknown names are ignored. Empty means the
        /// diff and the output tail.
        /// </summary>
        public List<string> StateFields
        {
            get => _StateFields;
            set => _StateFields = value ?? new List<string>();
        }

        /// <summary>The questions the decision asks. At least one is required to run.</summary>
        public List<CustomTypedQuestionSettings> Questions
        {
            get => _Questions;
            set => _Questions = value ?? new List<CustomTypedQuestionSettings>();
        }

        private double _GateThreshold = 0.9;
        private List<string> _StateFields = new List<string>();
        private List<CustomTypedQuestionSettings> _Questions = new List<CustomTypedQuestionSettings>();

        /// <summary>Deep-copy this definition, so a hot reload replaces values in place without sharing lists.</summary>
        /// <returns>An independent copy.</returns>
        public CustomTypedDecisionSettings Clone()
        {
            List<CustomTypedQuestionSettings> questions = new List<CustomTypedQuestionSettings>();
            foreach (CustomTypedQuestionSettings question in _Questions) questions.Add(question.Clone());
            return new CustomTypedDecisionSettings
            {
                Mode = Mode,
                GateThreshold = GateThreshold,
                RetainState = RetainState,
                Description = Description,
                Surface = Surface,
                Binding = Binding,
                StateFields = new List<string>(_StateFields),
                Questions = questions
            };
        }
    }

    /// <summary>
    /// One question in a custom typed decision. The three question kinds mirror the built-in ones:
    /// a Choice picks one named option, a Score rates on ordered levels, and a Noul gives the
    /// probability a statement is true. A Noul gates on its raw probability, so phrase it with the
    /// finding as its true pole.
    /// </summary>
    public class CustomTypedQuestionSettings
    {
        /// <summary>The question id, unique within the decision. Used as the answer key.</summary>
        public string Id { get; set; } = String.Empty;

        /// <summary>The question kind: <c>choice</c>, <c>score</c>, or <c>noul</c>.</summary>
        public string Type { get; set; } = "noul";

        /// <summary>The plain-language instruction the model answers.</summary>
        public string Instructions { get; set; } = String.Empty;

        /// <summary>For a choice, the allowed options as name to meaning. Ignored for other kinds.</summary>
        public Dictionary<string, string> Options
        {
            get => _Options;
            set => _Options = value ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        /// <summary>
        /// For a choice, the options that are the finding. A choice gates only when the model picks
        /// one of these, at the confidence it gave that option; with none named, the choice never
        /// gates. Each must be a key of <see cref="Options"/>. Ignored for other kinds.
        /// </summary>
        public List<string> FlagOptions
        {
            get => _FlagOptions;
            set => _FlagOptions = value ?? new List<string>();
        }

        /// <summary>For a score, the ordered level labels, lowest first. Ignored for other kinds.</summary>
        public List<string> Levels
        {
            get => _Levels;
            set => _Levels = value ?? new List<string>();
        }

        /// <summary>For a noul, the optional meaning of the high pole.</summary>
        public string? TrueMeaning { get; set; }

        /// <summary>For a noul, the optional meaning of the low pole.</summary>
        public string? FalseMeaning { get; set; }

        private Dictionary<string, string> _Options = new Dictionary<string, string>(StringComparer.Ordinal);
        private List<string> _Levels = new List<string>();
        private List<string> _FlagOptions = new List<string>();

        /// <summary>Deep-copy this question.</summary>
        /// <returns>An independent copy.</returns>
        public CustomTypedQuestionSettings Clone()
        {
            return new CustomTypedQuestionSettings
            {
                Id = Id,
                Type = Type,
                Instructions = Instructions,
                Options = new Dictionary<string, string>(_Options, StringComparer.Ordinal),
                Levels = new List<string>(_Levels),
                FlagOptions = new List<string>(_FlagOptions),
                TrueMeaning = TrueMeaning,
                FalseMeaning = FalseMeaning
            };
        }
    }
}
