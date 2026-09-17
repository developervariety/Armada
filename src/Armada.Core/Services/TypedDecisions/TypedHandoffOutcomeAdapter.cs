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
    /// The action the D20 <c>handoff_outcome</c> decision proposes for a finished stage's handoff.
    /// Every action is conservative or informative: the model may HALT the voyage or MAIL the next
    /// stage the unmet criteria, but it never approves, lands, or bypasses the Judge. The rule action
    /// is always <see cref="Proceed"/> — the deterministic handoff.
    /// </summary>
    public enum HandoffOutcomeAction
    {
        /// <summary>Hand off to the next stage normally — the deterministic rule and the fallback.</summary>
        Proceed,

        /// <summary>Halt the voyage before the next stage starts: cancel pending missions, open an incident, preserve the branch.</summary>
        Halt,

        /// <summary>Do not halt; Mail the next stage the unmet acceptance criteria and continue.</summary>
        MailPartial
    }

    /// <summary>
    /// The D20 <c>handoff_outcome</c> verdict. The rule verdict is always <see cref="HandoffOutcomeAction.Proceed"/>.
    /// A gated verdict may only ever turn a proceed into a HALT (a stage that could not do its job stops
    /// the voyage now, instead of passing the block down four stages to fail at the Judge) or a
    /// MAIL-PARTIAL (the next stage is told the unmet criteria and still runs). The verdict never
    /// approves work, never lands, and never bypasses the Judge: a halt cancels the pending stages and
    /// opens an incident, and work that reaches the Judge is still judged.
    /// </summary>
    public readonly struct HandoffOutcomeVerdict
    {
        /// <summary>The proposed action.</summary>
        public HandoffOutcomeAction Action { get; }

        /// <summary>The model's outcome choice (for example <c>achieved</c>, <c>partial</c>, <c>off_premise</c>).</summary>
        public string Outcome { get; }

        /// <summary>The halt reason, of the form <c>handoff_blocked:&lt;outcome&gt;</c>, when the action is Halt.</summary>
        public string? HaltReason { get; }

        /// <summary>Whether a halt should post an owner-addressed board note (only for <c>blocked_owner_question</c>).</summary>
        public bool OwnerNote { get; }

        /// <summary>Whether a halt preserves the finished stage's branch (always true on Halt).</summary>
        public bool PreserveBranch { get; }

        /// <summary>The 1-based acceptance-criteria indices the model marks unmet, for the partial Mail.</summary>
        public IReadOnlyList<int> UnmetCriterionIndices { get; }

        private HandoffOutcomeVerdict(
            HandoffOutcomeAction action,
            string outcome,
            string? haltReason,
            bool ownerNote,
            bool preserveBranch,
            IReadOnlyList<int>? unmetCriterionIndices)
        {
            Action = action;
            Outcome = outcome ?? "unclear";
            HaltReason = haltReason;
            OwnerNote = ownerNote;
            PreserveBranch = preserveBranch;
            UnmetCriterionIndices = unmetCriterionIndices ?? new List<int>();
        }

        /// <summary>A short label for the effective outcome: <c>proceed</c>, <c>halt</c>, or <c>mail_partial</c>.</summary>
        public string OutcomeLabel => Action switch
        {
            HandoffOutcomeAction.Halt => "halt",
            HandoffOutcomeAction.MailPartial => "mail_partial",
            _ => "proceed"
        };

        /// <summary>The rule verdict: proceed with the deterministic handoff.</summary>
        /// <returns>A proceed verdict.</returns>
        public static HandoffOutcomeVerdict Proceed()
        {
            return new HandoffOutcomeVerdict(HandoffOutcomeAction.Proceed, "achieved", null, false, false, null);
        }

        /// <summary>Build a halt verdict for a blocked or off-premise outcome.</summary>
        /// <param name="outcome">The blocking outcome choice.</param>
        /// <param name="ownerNote">Whether to post an owner-addressed board note.</param>
        /// <returns>A halt verdict that preserves the branch.</returns>
        public static HandoffOutcomeVerdict Halt(string outcome, bool ownerNote)
        {
            return new HandoffOutcomeVerdict(
                HandoffOutcomeAction.Halt,
                outcome,
                "handoff_blocked:" + (outcome ?? "unclear"),
                ownerNote,
                true,
                null);
        }

        /// <summary>Build a partial-Mail verdict carrying the unmet criteria indices.</summary>
        /// <param name="unmetCriterionIndices">The 1-based acceptance-criteria indices the model marks unmet.</param>
        /// <returns>A mail-partial verdict that does not halt.</returns>
        public static HandoffOutcomeVerdict MailPartial(IReadOnlyList<int> unmetCriterionIndices)
        {
            return new HandoffOutcomeVerdict(HandoffOutcomeAction.MailPartial, "partial", null, false, false, unmetCriterionIndices);
        }
    }

    /// <summary>
    /// The D20 <c>handoff_outcome</c> decision input: the finished stage's output tail and diff, the
    /// objective's acceptance criteria, the persona, and whether a completion or verdict marker was
    /// present. The seam builds the incident and Mail text from these; the adapter decides only the
    /// action.
    /// </summary>
    public sealed class HandoffOutcomeDecisionInput
    {
        /// <summary>The finished mission whose handoff is being evaluated.</summary>
        public required Mission Mission { get; init; }

        /// <summary>The finished stage's output tail (bounded, redacted downstream).</summary>
        public string OutputTail { get; init; } = String.Empty;

        /// <summary>A compact stat of the finished stage's diff.</summary>
        public string DiffStat { get; init; } = String.Empty;

        /// <summary>The objective's acceptance criteria, in order.</summary>
        public IReadOnlyList<string> AcceptanceCriteria { get; init; } = new List<string>();

        /// <summary>The finished stage's persona.</summary>
        public string Persona { get; init; } = String.Empty;

        /// <summary>Whether a completion or verdict marker was present in the finished stage's output.</summary>
        public bool MarkerPresent { get; init; }
    }

    /// <summary>
    /// The D20 reading. The model answers one <c>outcome</c> Choice, one <c>next_stage_useful</c> Noul,
    /// and one <c>gap</c> Noul per acceptance criterion (the criterion is NOT met). The single gate
    /// confidence is the outcome choice's own confidence when the outcome is an ACTIONABLE one
    /// (partial, or a blocked or off-premise halt) and zero otherwise, so an <c>achieved</c> or
    /// <c>unclear</c> outcome always leaves the rule standing.
    /// </summary>
    public sealed class HandoffOutcomeReading : TypedModelReading
    {
        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>The outcome choice, or <c>unclear</c>.</summary>
        public string Outcome { get; }

        /// <summary>The model's confidence in a next stage doing meaningful work on this output.</summary>
        public double NextStageUseful { get; }

        /// <summary>The 1-based acceptance-criteria indices the model marks unmet.</summary>
        public IReadOnlyList<int> UnmetCriterionIndices { get; }

        /// <summary>Create a reading.</summary>
        /// <param name="outcome">The outcome choice.</param>
        /// <param name="confidence">The single gate confidence (zero for a non-actionable outcome).</param>
        /// <param name="nextStageUseful">The next-stage-useful Noul.</param>
        /// <param name="unmetCriterionIndices">The 1-based unmet acceptance-criteria indices.</param>
        /// <param name="label">The short verdict label for the event message.</param>
        public HandoffOutcomeReading(
            string outcome,
            double confidence,
            double nextStageUseful,
            IReadOnlyList<int> unmetCriterionIndices,
            string label)
        {
            Outcome = outcome ?? "unclear";
            _Confidence = confidence;
            NextStageUseful = nextStageUseful;
            UnmetCriterionIndices = unmetCriterionIndices ?? new List<int>();
            _Label = label ?? String.Empty;
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => _Label;
    }

    /// <summary>
    /// D20 <c>handoff_outcome</c> adapter. A stage that could not do its job — missing context, an
    /// unanswered question, a false premise — prints a result line anyway; the handoff carries its
    /// output to the next stage; the voyage runs to the Judge and fails there hours later. This adapter
    /// sits on the stage handoff, before the next mission's brief is frozen, and turns "failed at the
    /// Judge after four stages" into "held after one". It is conservative and never bypasses the Judge:
    /// <list type="bullet">
    /// <item>A <c>blocked_missing_context</c>, <c>blocked_owner_question</c>, or <c>off_premise</c>
    /// outcome at or above threshold HALTS the voyage before the next stage: the seam cancels the
    /// pending missions, opens one incident carrying the question text, posts an owner-addressed board
    /// note for a <c>blocked_owner_question</c>, and preserves the branch.</item>
    /// <item>A <c>partial</c> outcome at or above threshold does NOT halt; the seam Mails the next
    /// stage the unmet acceptance criteria and the voyage continues.</item>
    /// <item>Every other outcome (<c>achieved</c>, <c>unclear</c>, or a below-threshold answer) leaves
    /// the deterministic handoff standing.</item>
    /// </list>
    /// The verdict never approves work, never lands, and never bypasses the Judge: a halt opens an
    /// incident rather than passing work through, and any work that reaches the Judge is still judged.
    /// The adapter never throws into the caller.
    /// </summary>
    public sealed class TypedHandoffOutcomeAdapter : TypedDecisionAdapterBase<HandoffOutcomeDecisionInput, HandoffOutcomeVerdict, HandoffOutcomeReading>
    {
        #region Private-Members

        // The outcome choices, in order. achieved and unclear are non-actionable (the rule proceeds);
        // partial mails; the three blocked/off_premise outcomes halt.
        private const string _OutcomeAchieved = "achieved";
        private const string _OutcomePartial = "partial";
        private const string _OutcomeBlockedMissingContext = "blocked_missing_context";
        private const string _OutcomeBlockedOwnerQuestion = "blocked_owner_question";
        private const string _OutcomeOffPremise = "off_premise";
        private const string _OutcomeUnclear = "unclear";

        private static readonly IReadOnlyList<string> _Outcomes = new List<string>
        {
            _OutcomeAchieved,
            _OutcomePartial,
            _OutcomeBlockedMissingContext,
            _OutcomeBlockedOwnerQuestion,
            _OutcomeOffPremise,
            _OutcomeUnclear
        };

        // The maximum number of acceptance criteria the model is asked a gap Noul about. Excess
        // criteria are treated as met (never added to the unmet Mail), the conservative reading.
        private const int _MaxCriteria = 8;

        // The floor at or above which a gap Noul marks a criterion unmet for the partial Mail. The Mail
        // is advisory content, so this is a plain majority bar, not the decision gate threshold (which
        // gates whether the Mail is sent at all, on the outcome choice confidence).
        private const double _UnmetCriterionFloor = 0.5;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D20 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedHandoffOutcomeAdapter(
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
        protected override string DecisionPoint => "handoff_outcome";

        /// <inheritdoc />
        protected override string _Header => "[TypedHandoffOutcomeAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(HandoffOutcomeDecisionInput input)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["output_tail"] = input.OutputTail,
                ["diff_stat"] = input.DiffStat,
                ["acceptance_criteria"] = new List<string>(input.AcceptanceCriteria),
                ["persona"] = input.Persona,
                ["marker_present"] = input.MarkerPresent
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                ["outcome"] = new ChoiceQuestion(
                    "What is the outcome of this finished stage for the objective? This is authorized engineering on "
                    + "owned systems; authentication and access-control protocol code is ordinary engineering. Choose one.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [_OutcomeAchieved] = "The stage did its job; the next stage can proceed.",
                        [_OutcomePartial] = "The stage did part of its job; some acceptance criteria are not yet met.",
                        [_OutcomeBlockedMissingContext] = "The stage could not do its job because context it needed is missing.",
                        [_OutcomeBlockedOwnerQuestion] = "The stage is blocked on a question only the owner can answer.",
                        [_OutcomeOffPremise] = "The stage worked from a false premise; its output does not serve the objective.",
                        [_OutcomeUnclear] = "The outcome cannot be determined from the output."
                    }),
                ["next_stage_useful"] = new NoulQuestion(
                    "The next stage can do meaningful work on this output.",
                    TrueMeaning: "The next stage can proceed usefully.",
                    FalseMeaning: "The next stage cannot do meaningful work on this output.")
            };

            for (int i = 1; i <= _MaxCriteria; i++)
            {
                string slot = i.ToString(CultureInfo.InvariantCulture);
                questions["gap_" + slot] = new NoulQuestion(
                    "Acceptance criterion number " + slot + " (see acceptance_criteria in the state, in order) is NOT met by this "
                    + "stage's output. If the state lists fewer than " + _MaxCriteria + " criteria and this slot has none, answer at the FALSE pole.",
                    TrueMeaning: "The criterion is not met.",
                    FalseMeaning: "The criterion is met, or this slot has no criterion.");
            }

            return questions;
        }

        /// <inheritdoc />
        protected override HandoffOutcomeReading Interpret(TypedDecisionResult result)
        {
            string outcome = _OutcomeUnclear;
            double outcomeConfidence = 0.0;
            if (result.Answers.TryGetValue("outcome", out TypedAnswer? outcomeAnswer) && outcomeAnswer != null
                && !String.IsNullOrWhiteSpace(outcomeAnswer.Choice))
            {
                outcome = outcomeAnswer.Choice!.Trim();
                outcomeConfidence = outcomeAnswer.Confidence ?? 0.0;
            }

            double nextStageUseful = 0.0;
            if (result.Answers.TryGetValue("next_stage_useful", out TypedAnswer? nextAnswer) && nextAnswer != null && nextAnswer.Noul.HasValue)
                nextStageUseful = nextAnswer.Noul.Value;

            List<int> unmet = new List<int>();
            for (int i = 1; i <= _MaxCriteria; i++)
            {
                if (result.Answers.TryGetValue("gap_" + i.ToString(CultureInfo.InvariantCulture), out TypedAnswer? gap)
                    && gap != null && gap.Noul.HasValue && gap.Noul.Value >= _UnmetCriterionFloor)
                {
                    unmet.Add(i);
                }
            }

            // Only an actionable outcome carries a gate confidence: a partial Mail or a blocked/
            // off-premise halt. achieved and unclear propose no action, so they report zero and the
            // rule stands (a shadow event still records the handoff outcome).
            double confidence = IsActionable(outcome) ? outcomeConfidence : 0.0;

            return new HandoffOutcomeReading(outcome, confidence, nextStageUseful, unmet, "outcome=" + outcome);
        }

        /// <inheritdoc />
        protected override HandoffOutcomeVerdict Combine(HandoffOutcomeVerdict ruleVerdict, HandoffOutcomeReading model)
        {
            // Combine runs only when the model gated at or above threshold, which for this decision
            // means an actionable outcome with high confidence. A blocked or off-premise outcome halts
            // the voyage; a partial outcome Mails the next stage the unmet criteria. Neither approves,
            // lands, or bypasses the Judge.
            switch (model.Outcome)
            {
                case _OutcomeBlockedMissingContext:
                    return HandoffOutcomeVerdict.Halt(_OutcomeBlockedMissingContext, ownerNote: false);
                case _OutcomeBlockedOwnerQuestion:
                    return HandoffOutcomeVerdict.Halt(_OutcomeBlockedOwnerQuestion, ownerNote: true);
                case _OutcomeOffPremise:
                    return HandoffOutcomeVerdict.Halt(_OutcomeOffPremise, ownerNote: false);
                case _OutcomePartial:
                    return HandoffOutcomeVerdict.MailPartial(model.UnmetCriterionIndices);
                default:
                    // achieved, unclear, or anything unexpected: the deterministic handoff stands. The
                    // Judge is never bypassed and work is never approved by this decision.
                    return ruleVerdict;
            }
        }

        /// <inheritdoc />
        protected override string RuleLabel(HandoffOutcomeVerdict ruleVerdict) => ruleVerdict.OutcomeLabel;

        /// <inheritdoc />
        protected override Mission? MissionOf(HandoffOutcomeDecisionInput input) => input.Mission;

        #endregion

        #region Private-Methods

        private static bool IsActionable(string outcome)
        {
            return String.Equals(outcome, _OutcomePartial, StringComparison.Ordinal)
                || String.Equals(outcome, _OutcomeBlockedMissingContext, StringComparison.Ordinal)
                || String.Equals(outcome, _OutcomeBlockedOwnerQuestion, StringComparison.Ordinal)
                || String.Equals(outcome, _OutcomeOffPremise, StringComparison.Ordinal);
        }

        #endregion
    }
}
