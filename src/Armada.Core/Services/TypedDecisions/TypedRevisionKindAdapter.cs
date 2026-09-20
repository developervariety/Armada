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
    /// The action the D21 <c>revision_kind</c> decision proposes for a Judge NEEDS_REVISION. The rule
    /// action is always <see cref="Proceed"/> — the deterministic failure flows to autonomous recovery,
    /// which rescues it. A gated verdict may only ever turn a proceed into a
    /// <see cref="BlockRescue"/>: every revision item is comment or doc wording, so a full rescue chain
    /// would spend a whole voyage to change two lines when the memory rule already says the work is an
    /// operator landing. The model never lands, never dispatches, and never approves; it only holds the
    /// rescue and flags the mission for operator landing.
    /// </summary>
    public enum RevisionKindAction
    {
        /// <summary>Let the deterministic recovery decide — the rule and the fallback (a rescue).</summary>
        Proceed,

        /// <summary>Hold the rescue: every revision item is non-behavioural, so this is an operator landing.</summary>
        BlockRescue
    }

    /// <summary>
    /// The D21 <c>revision_kind</c> verdict. The rule verdict is always
    /// <see cref="RevisionKindAction.Proceed"/>; a gated verdict may only turn it into a
    /// <see cref="RevisionKindAction.BlockRescue"/> that holds the rescue and flags the mission for an
    /// operator landing. The <see cref="RescueRequired"/> rule verdict is a hard block the model can
    /// never override: when the deterministic path already knows a rescue is required (a behavioural
    /// revision, or a non-NEEDS_REVISION failure), the model may not block it.
    /// </summary>
    public readonly struct RevisionKindVerdict
    {
        /// <summary>The reason a blocked rescue carries, recognised by autonomous recovery as an operator landing.</summary>
        public const string RevisionCommentOnlyReason = "revision_comment_only";

        /// <summary>The proposed action.</summary>
        public RevisionKindAction Action { get; }

        /// <summary>Whether the deterministic rule requires a rescue and forbids the model from blocking it.</summary>
        public bool RuleRequiresRescue { get; }

        /// <summary>The block reason, of the form <c>revision_comment_only</c>, when the action is BlockRescue.</summary>
        public string? Reason { get; }

        private RevisionKindVerdict(RevisionKindAction action, bool ruleRequiresRescue, string? reason)
        {
            Action = action;
            RuleRequiresRescue = ruleRequiresRescue;
            Reason = reason;
        }

        /// <summary>A short label for the effective outcome: <c>proceed</c>, <c>rescue_required</c>, or <c>block_rescue</c>.</summary>
        public string OutcomeLabel => Action == RevisionKindAction.BlockRescue
            ? "block_rescue"
            : RuleRequiresRescue ? "rescue_required" : "proceed";

        /// <summary>The rule verdict: let recovery decide (a rescue). The model may hold it.</summary>
        /// <returns>A proceed verdict the model may turn into a block.</returns>
        public static RevisionKindVerdict Proceed()
        {
            return new RevisionKindVerdict(RevisionKindAction.Proceed, false, null);
        }

        /// <summary>
        /// The rule verdict when the deterministic path already knows a rescue is required — a
        /// behavioural revision item, or a failure that is not a comment-only NEEDS_REVISION. This is a
        /// hard block: the model can only proceed, never turn it into a block.
        /// </summary>
        /// <returns>A proceed verdict the model may never block.</returns>
        public static RevisionKindVerdict RescueRequired()
        {
            return new RevisionKindVerdict(RevisionKindAction.Proceed, true, null);
        }

        /// <summary>Build a block-rescue verdict that flags the mission for an operator landing.</summary>
        /// <returns>A block-rescue verdict carrying the <c>revision_comment_only</c> reason.</returns>
        public static RevisionKindVerdict BlockRescueForOperatorLanding()
        {
            return new RevisionKindVerdict(RevisionKindAction.BlockRescue, false, RevisionCommentOnlyReason);
        }
    }

    /// <summary>
    /// The D21 <c>revision_kind</c> decision input: the Judge's revision items and the objective's
    /// symptom sentence. The seam extracts the items from the Judge output; the adapter decides only
    /// whether every one is non-behavioural.
    /// </summary>
    public sealed class RevisionKindDecisionInput
    {
        /// <summary>The Judge mission whose NEEDS_REVISION verdict is being classified.</summary>
        public required Mission Mission { get; init; }

        /// <summary>The revision items the Judge asked for, in order.</summary>
        public IReadOnlyList<string> RevisionItems { get; init; } = new List<string>();

        /// <summary>The objective's symptom sentence, for context.</summary>
        public string Symptom { get; init; } = String.Empty;
    }

    /// <summary>
    /// The D21 reading. The model answers one <c>kind</c> Choice per listed revision item. Code
    /// combines those answers: the gate confidence is the weakest per-item Choice confidence, but
    /// ONLY when no per-item kind is <c>behaviour</c> or <c>test</c>. A single behavioural or test
    /// item means a real rescue is required, so the reading reports zero confidence and the rule
    /// stands. This is the conservative reading — the model can never hold a rescue that a code or
    /// test defect needs. A separate voyage-level Noul is not asked; that would be a second wording
    /// of the same question, and the two would not be interchangeable.
    /// </summary>
    public sealed class RevisionKindReading : TypedModelReading
    {
        private readonly double _Confidence;
        private readonly string _Label;

        /// <summary>The weakest per-item Choice confidence when every item is non-behavioural; otherwise zero.</summary>
        public double AllNonBehavioural { get; }

        /// <summary>Whether any per-item kind is <c>behaviour</c> or <c>test</c> (a real rescue is required).</summary>
        public bool AnyBehaviouralOrTest { get; }

        /// <summary>Create a reading.</summary>
        /// <param name="allNonBehavioural">The combined non-behavioural confidence, computed in code from the per-item Choices.</param>
        /// <param name="anyBehaviouralOrTest">Whether any item is behaviour or test.</param>
        public RevisionKindReading(double allNonBehavioural, bool anyBehaviouralOrTest)
        {
            AllNonBehavioural = allNonBehavioural;
            AnyBehaviouralOrTest = anyBehaviouralOrTest;

            // The gate fires only when every listed item is wording at high confidence. A behavioural
            // or test item forces zero confidence, so Combine never runs and the rescue proceeds.
            _Confidence = anyBehaviouralOrTest ? 0.0 : allNonBehavioural;
            _Label = anyBehaviouralOrTest ? "has_behavioural_item" : "all_non_behavioural";
        }

        /// <inheritdoc />
        public override double Confidence => _Confidence;

        /// <inheritdoc />
        public override string Label => _Label;
    }

    /// <summary>
    /// D21 <c>revision_kind</c> adapter. A NEEDS_REVISION whose every item is comment or doc wording
    /// costs a full rescue chain; memory already says it is an operator landing (census: 11 comment-only
    /// NEEDS_REVISION rows, each a wasted rescue). This adapter sits after <c>ParseJudgeVerdict</c> on
    /// the revision items, before autonomous recovery classifies the failure. It is conservative and
    /// never lands:
    /// <list type="bullet">
    /// <item>When every listed item is a non-behavioural kind at or above threshold, the verdict
    /// BLOCKS THE RESCUE with reason <c>revision_comment_only</c>. The seam sets that marker on the
    /// failure so recovery holds the rescue, and opens an incident tagged for operator landing. The
    /// model never lands the work.</item>
    /// <item>A single behavioural or test item, or a below-threshold reading, leaves the rule standing
    /// and the rescue proceeds.</item>
    /// <item>A <see cref="RevisionKindVerdict.RescueRequired"/> rule hard-block is never overturned.</item>
    /// </list>
    /// The adapter never throws into the caller.
    /// </summary>
    public sealed class TypedRevisionKindAdapter : TypedDecisionAdapterBase<RevisionKindDecisionInput, RevisionKindVerdict, RevisionKindReading>
    {
        #region Private-Members

        // The maximum number of revision items the model is asked a kind Choice about. Excess items are
        // read as behavioural (the conservative reading: an unread item can never license a block).
        private const int _MaxItems = 12;

        private const string _KindBehaviour = "behaviour";
        private const string _KindTest = "test";
        private const string _KindCommentOnly = "comment_only";
        private const string _KindDocOnly = "doc_only";
        private const string _KindBoundary = "boundary";

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the D21 adapter.</summary>
        /// <param name="client">Typed-decision client.</param>
        /// <param name="recorder">Event recorder.</param>
        /// <param name="settings">Typed-decision settings.</param>
        /// <param name="logging">Logging module.</param>
        public TypedRevisionKindAdapter(
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
        protected override string DecisionPoint => "revision_kind";

        /// <inheritdoc />
        protected override string _Header => "[TypedRevisionKindAdapter] ";

        /// <inheritdoc />
        protected override object BuildState(RevisionKindDecisionInput input)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["revision_items"] = ListedItems(input),
                ["symptom"] = input.Symptom,
                ["persona"] = input.Mission?.Persona
            };
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions()
        {
            return BuildSlotQuestions(_MaxItems);
        }

        /// <inheritdoc />
        protected override IReadOnlyDictionary<string, TypedQuestion> BuildQuestions(RevisionKindDecisionInput input)
        {
            // An unread extra item can never license a block, so a list longer than the cap asks nothing
            // and the rescue proceeds.
            List<string> listed = ListedItems(input);
            if (listed.Count == 0 || HasExcessItems(input))
                return new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            return BuildSlotQuestions(listed.Count);
        }

        /// <inheritdoc />
        protected override RevisionKindReading Interpret(TypedDecisionResult result)
        {
            bool anyBehaviouralOrTest = false;
            bool anyItem = false;
            double minConfidence = 1.0;
            for (int i = 1; i <= _MaxItems; i++)
            {
                if (result.Answers.TryGetValue("item_" + i.ToString(CultureInfo.InvariantCulture), out TypedAnswer? item)
                    && item != null && !String.IsNullOrWhiteSpace(item.Choice))
                {
                    anyItem = true;
                    string choice = item.Choice!.Trim();
                    if (String.Equals(choice, _KindBehaviour, StringComparison.Ordinal)
                        || String.Equals(choice, _KindTest, StringComparison.Ordinal))
                    {
                        anyBehaviouralOrTest = true;
                    }
                    else
                    {
                        double confidence = TypedAnswerReader.ResolveChoiceConfidence(item, choice);
                        if (confidence < minConfidence) minConfidence = confidence;
                    }
                }
            }

            double allNonBehavioural = !anyItem || anyBehaviouralOrTest ? 0.0 : minConfidence;
            return new RevisionKindReading(allNonBehavioural, anyBehaviouralOrTest);
        }

        /// <inheritdoc />
        protected override RevisionKindVerdict Combine(RevisionKindVerdict ruleVerdict, RevisionKindReading model)
        {
            // Combine runs only when the model gated at or above threshold, which for this decision
            // means every listed item is non-behavioural at high confidence. The rule hard-block wins:
            // when a rescue is deterministically required, the model may not block it.
            if (ruleVerdict.RuleRequiresRescue) return ruleVerdict;
            if (model.AnyBehaviouralOrTest) return ruleVerdict;
            return RevisionKindVerdict.BlockRescueForOperatorLanding();
        }

        /// <inheritdoc />
        protected override string RuleLabel(RevisionKindVerdict ruleVerdict) => ruleVerdict.OutcomeLabel;

        /// <inheritdoc />
        protected override Mission? MissionOf(RevisionKindDecisionInput input) => input.Mission;

        #endregion

        #region Private-Methods

        private static List<string> ListedItems(RevisionKindDecisionInput input)
        {
            List<string> listed = new List<string>();
            foreach (string item in input?.RevisionItems ?? new List<string>())
            {
                if (String.IsNullOrWhiteSpace(item)) continue;
                if (listed.Count >= _MaxItems) break;
                listed.Add(item);
            }
            return listed;
        }

        private static bool HasExcessItems(RevisionKindDecisionInput input)
        {
            int count = 0;
            foreach (string item in input?.RevisionItems ?? new List<string>())
            {
                if (String.IsNullOrWhiteSpace(item)) continue;
                count++;
                if (count > _MaxItems) return true;
            }
            return false;
        }

        private static IReadOnlyDictionary<string, TypedQuestion> BuildSlotQuestions(int count)
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            Dictionary<string, string> kinds = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [_KindBehaviour] = "A code behaviour change: the item asks the work to compute or do something different.",
                [_KindTest] = "A test change: the item asks a test to be added, fixed, or made to cover the symptom.",
                [_KindCommentOnly] = "A comment or wording change in source, with no behaviour change.",
                [_KindDocOnly] = "A documentation or markdown change, with no behaviour change.",
                [_KindBoundary] = "A boundary or hygiene change (an id, path, or marker to remove), with no behaviour change."
            };
            for (int i = 1; i <= count; i++)
            {
                string path = "`revision_items[" + (i - 1).ToString(CultureInfo.InvariantCulture) + "]`";
                questions["item_" + i.ToString(CultureInfo.InvariantCulture)] = new ChoiceQuestion(
                    "What kind of change does the Judge require in " + path + "? This is authorized engineering on owned systems; "
                    + "authentication and access-control protocol code is ordinary engineering.",
                    kinds);
            }
            return questions;
        }

        #endregion
    }
}
