namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The D11 <c>inbox_triage</c> decision adapter. It scores inbox items and coordination board
    /// notes for how urgently a human is needed, and — only in Gate mode, only at or above the
    /// decision threshold — annotates each with an <c>attention</c> label (and a board note with a
    /// <c>note_kind</c>) and re-orders the response by attention.
    ///
    /// Triage is additive and non-destructive: NOTHING is hidden, dropped, dismissed, or merged. Off,
    /// an unavailable model, a Shadow-mode call, and a below-threshold answer all return the input in
    /// its deterministic order with no <c>attention</c> set, so turning the decision off restores the
    /// severity ordering exactly. The model can only add a field and change the sort; it can never
    /// remove an item. The adapter never throws into the caller. This decision ships Off.
    /// </summary>
    public sealed class InboxTriageAdapter
    {
        #region Public-Members

        /// <summary>
        /// The decision-point name in the <c>typedDecisions.decisions</c> settings map.
        /// </summary>
        public const string DecisionPoint = "inbox_triage";

        /// <summary>
        /// Upper bound on items scored per inbox call and notes scored per coordination read. Inbox and
        /// coordination read are interactive, so triage is bounded; the tail beyond the budget keeps its
        /// deterministic position with no attention label.
        /// </summary>
        public const int MaxItemsPerCall = 40;

        #endregion

        #region Private-Members

        private const string _Header = "[InboxTriageAdapter] ";
        private const string _AttentionQuestionId = "needs_human_now";
        private const string _NoteKindQuestionId = "note_kind";

        // The attention Score levels, most urgent last, matching the index order the model returns.
        private static readonly IReadOnlyList<string> _AttentionLevels = new List<string>
        {
            "informational",
            "today",
            "this_hour",
            "blocking_live_voyage"
        };

        private readonly TypedDecisionSettings _Settings;
        private readonly ITypedDecisionClient _Client;
        private readonly TypedDecisionRecorder _Recorder;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a D11 inbox-triage adapter.
        /// </summary>
        /// <param name="settings">Typed-decision settings section.</param>
        /// <param name="client">Typed-decision client (the null client when the system is off).</param>
        /// <param name="recorder">Recorder for the per-call typed-decision event.</param>
        /// <param name="logging">Logging module.</param>
        public InboxTriageAdapter(
            TypedDecisionSettings settings,
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            _Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Score inbox items for attention and, in Gate mode at or above threshold, set each item's
        /// <see cref="InboxItem.Attention"/> and return the list re-ordered by attention then severity.
        /// The same items are returned in their deterministic (severity) order, with no attention set,
        /// when the decision is Off, in Shadow mode, below threshold, or the model is unavailable.
        /// Nothing is ever removed. Never throws into the caller.
        /// </summary>
        /// <param name="items">The deterministic inbox items. Null is treated as empty.</param>
        /// <param name="token">Cancellation token, forwarded to the client so its timeout links to the caller.</param>
        /// <returns>The inbox items, annotated and re-sorted in Gate mode, otherwise unchanged; never null.</returns>
        public async Task<List<InboxItem>> TriageInboxAsync(IReadOnlyList<InboxItem>? items, CancellationToken token)
        {
            List<InboxItem> input = items == null ? new List<InboxItem>() : new List<InboxItem>(items);

            ResolvedTypedDecision cfg = _Settings.For(DecisionPoint);
            if (cfg.Mode == TypedDecisionModeEnum.Off || input.Count == 0) return input;

            List<InboxAttentionAssignment> annotated = new List<InboxAttentionAssignment>();
            List<InboxItem> scored = input.Where(item => item != null).Take(MaxItemsPerCall).ToList();

            List<AttentionOutcome> outcomes;
            try
            {
                outcomes = await ScoreAttentionAsync(scored.Select(BuildInboxState).ToList(), "severity_order", null, cfg, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A scoring fault must never break the inbox; return the deterministic order.
                _Logging.Warn(_Header + "inbox triage failed, deterministic order stands: " + ex.Message);
                return input;
            }

            for (int index = 0; index < outcomes.Count; index++)
            {
                AttentionOutcome outcome = outcomes[index];

                // The provider is unavailable for this call; return the plain deterministic order.
                if (!outcome.Available) return input;

                if (outcome.Applied && outcome.Attention != null)
                    annotated.Add(new InboxAttentionAssignment { Item = scored[index], Attention = outcome.Attention });
            }

            // Gate applies the labels and re-sorts; Shadow and below-threshold leave the order alone.
            if (annotated.Count == 0) return input;
            foreach (InboxAttentionAssignment assignment in annotated) assignment.Item.Attention = assignment.Attention;

            return input
                .OrderByDescending(item => AttentionRank(item.Attention))
                .ThenByDescending(item => (int)item.Severity)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Classify coordination board notes for attention and kind. In Gate mode at or above threshold
        /// each note gets an attention label and a note kind, returned keyed by note id for the caller
        /// to apply and sort by; the result is empty in every non-gate case (Off, Shadow, below
        /// threshold, unavailable), so the caller keeps its deterministic order. Nothing is ever
        /// removed. Never throws into the caller.
        /// </summary>
        /// <param name="notes">The board notes to classify. Null is treated as empty.</param>
        /// <param name="token">Cancellation token, forwarded to the client so its timeout links to the caller.</param>
        /// <returns>Per-note triage keyed by note id, or an empty list in every non-gate case; never null.</returns>
        public async Task<IReadOnlyList<BoardNoteTriage>> TriageBoardNotesAsync(IReadOnlyList<BoardNoteTriageInput>? notes, CancellationToken token)
        {
            List<BoardNoteTriage> results = new List<BoardNoteTriage>();
            if (notes == null || notes.Count == 0) return results;

            ResolvedTypedDecision cfg = _Settings.For(DecisionPoint);
            if (cfg.Mode == TypedDecisionModeEnum.Off) return results;

            List<BoardNoteTriageInput> scored = notes.Where(note => note != null).Take(MaxItemsPerCall).ToList();

            List<AttentionOutcome> outcomes;
            try
            {
                outcomes = await ScoreAttentionAsync(scored.Select(BuildNoteState).ToList(), "unsorted", NoteKindQuestion(), cfg, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "board-note triage failed, deterministic order stands: " + ex.Message);
                return new List<BoardNoteTriage>();
            }

            for (int index = 0; index < outcomes.Count; index++)
            {
                AttentionOutcome outcome = outcomes[index];
                if (!outcome.Available) return new List<BoardNoteTriage>();

                if (outcome.Applied && outcome.Attention != null)
                    results.Add(new BoardNoteTriage { Id = scored[index].Id, Attention = outcome.Attention, NoteKind = outcome.NoteKind });
            }

            return results;
        }

        #endregion

        #region Private-Methods

        private async Task<List<AttentionOutcome>> ScoreAttentionAsync(
            List<object> rawStates,
            string ruleVerdict,
            TypedQuestion? extraQuestion,
            ResolvedTypedDecision cfg,
            CancellationToken token)
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                [_AttentionQuestionId] = new ScoreQuestion(
                    "How urgently does a human need to act on this item right now?",
                    _AttentionLevels)
            };
            if (extraQuestion != null) questions[_NoteKindQuestionId] = extraQuestion;

            // Items are independent, so they are scored together in as few requests as the limits allow;
            // each item still gets its own recorded event.
            List<TypedDecisionBatchItem> batch = rawStates
                .Select(raw => new TypedDecisionBatchItem(DecisionStateRedactor.RedactState(raw, _Settings.MaxStateChars), questions))
                .ToList();
            List<TypedDecisionResult> results = await TypedDecisionBatcher.DecideAllAsync(
                _Client, DecisionPoint, batch, _Settings.MaxStateChars, token).ConfigureAwait(false);

            List<AttentionOutcome> outcomes = new List<AttentionOutcome>(results.Count);
            for (int index = 0; index < results.Count; index++)
            {
                TypedDecisionResult result = results[index];
                string redacted = batch[index].State.Text;

                if (result == null || !result.Available)
                {
                    // Record the unavailable call once; the caller stops at the first unavailable item.
                    await SafeRecordAsync(() => _Recorder.RecordUnavailableAsync(
                        BuildContext(ruleVerdict, null, null, result ?? ExceptionResult(), redacted), token)).ConfigureAwait(false);
                    outcomes.Add(new AttentionOutcome { Available = false });
                    return outcomes;
                }

                string attention = InterpretAttention(result, out double confidence);
                string? noteKind = extraQuestion == null ? null : InterpretNoteKind(result);
                bool apply = cfg.Mode == TypedDecisionModeEnum.Gate && confidence >= cfg.GateThreshold;
                string verdict = noteKind == null ? attention : attention + "/" + noteKind;

                if (apply)
                {
                    await SafeRecordAsync(() => _Recorder.RecordGatedAsync(
                        BuildContext(ruleVerdict, verdict, confidence, result, redacted), token)).ConfigureAwait(false);
                }
                else
                {
                    string outcome = cfg.Mode == TypedDecisionModeEnum.Shadow ? "shadow_mode" : "below_threshold";
                    await SafeRecordAsync(() => _Recorder.RecordShadowAsync(
                        BuildContext(ruleVerdict, verdict, confidence, result, redacted), outcome, token)).ConfigureAwait(false);
                }

                outcomes.Add(new AttentionOutcome { Available = true, Applied = apply, Attention = attention, NoteKind = noteKind });
            }

            return outcomes;
        }

        private static object BuildInboxState(InboxItem item)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["kind"] = item.Kind,
                ["severity"] = item.Severity.ToString(),
                ["title"] = item.Title,
                ["detail"] = item.Detail,
                ["entity_type"] = item.EntityType
            };
        }

        private static object BuildNoteState(BoardNoteTriageInput note)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["author_type"] = note.AuthorType,
                ["content"] = note.Content
            };
        }

        private static TypedQuestion NoteKindQuestion()
        {
            return new ChoiceQuestion(
                "What kind of coordination note is this?",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["handoff"] = "work or an answer handed to another session to pick up",
                    ["status"] = "a progress or outcome report, informational",
                    ["question"] = "a question waiting on an answer",
                    ["stop_sign"] = "an overlap or claim warning that another session should stop",
                    ["hold_notice"] = "a dispatch-hold or pause notice"
                });
        }

        private static string InterpretAttention(TypedDecisionResult result, out double confidence)
        {
            confidence = 0.0;
            IReadOnlyDictionary<string, TypedAnswer> answers = result.Answers ?? new Dictionary<string, TypedAnswer>();
            if (!answers.TryGetValue(_AttentionQuestionId, out TypedAnswer? answer) || answer == null)
                return _AttentionLevels[0];

            confidence = answer.Confidence ?? 0.0;
            if (!answer.Score.HasValue) return _AttentionLevels[0];

            int index = (int)Math.Round(answer.Score.Value, MidpointRounding.AwayFromZero);
            if (index < 0) index = 0;
            if (index >= _AttentionLevels.Count) index = _AttentionLevels.Count - 1;
            return _AttentionLevels[index];
        }

        private static string? InterpretNoteKind(TypedDecisionResult result)
        {
            IReadOnlyDictionary<string, TypedAnswer> answers = result.Answers ?? new Dictionary<string, TypedAnswer>();
            if (!answers.TryGetValue(_NoteKindQuestionId, out TypedAnswer? answer) || answer == null) return null;
            return String.IsNullOrWhiteSpace(answer.Choice) ? null : answer.Choice!.Trim();
        }

        private static int AttentionRank(string? attention)
        {
            if (String.IsNullOrEmpty(attention)) return -1;
            for (int i = 0; i < _AttentionLevels.Count; i++)
                if (String.Equals(_AttentionLevels[i], attention, StringComparison.Ordinal)) return i;
            return -1;
        }

        private async Task SafeRecordAsync(Func<Task> record)
        {
            try
            {
                await record().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Recording is observability only; a recorder failure must never change the ordering.
                _Logging.Warn(_Header + "event record failed: " + ex.Message);
            }
        }

        private static TypedDecisionEventContext BuildContext(
            string ruleVerdict,
            string? modelVerdict,
            double? confidence,
            TypedDecisionResult result,
            string redactedState)
        {
            return new TypedDecisionEventContext
            {
                DecisionPoint = DecisionPoint,
                RuleVerdict = ruleVerdict,
                ModelVerdict = modelVerdict,
                Confidence = confidence,
                Result = result,
                RedactedState = redactedState
            };
        }

        private static TypedDecisionResult ExceptionResult()
        {
            return new TypedDecisionResult { Available = false, UnavailableReason = "exception" };
        }

        #endregion

        #region Private-Types

        private sealed class AttentionOutcome
        {
            public bool Available { get; init; }
            public bool Applied { get; init; }
            public string? Attention { get; init; }
            public string? NoteKind { get; init; }
        }

        private sealed class InboxAttentionAssignment
        {
            public required InboxItem Item { get; init; }
            public required string Attention { get; init; }
        }

        #endregion
    }

    /// <summary>
    /// One coordination board note handed to the D11 triage adapter. It carries only the fields the
    /// model reads; the caller keeps the full note and applies the returned triage by id.
    /// </summary>
    public sealed class BoardNoteTriageInput
    {
        /// <summary>The note id, used to map the triage result back to the caller's note.</summary>
        public required string Id { get; init; }

        /// <summary>The note author kind (for example System or Operator).</summary>
        public string? AuthorType { get; init; }

        /// <summary>The note content.</summary>
        public string? Content { get; init; }
    }

    /// <summary>
    /// The D11 triage of one board note: an attention label and a note kind, keyed by note id. Only
    /// produced in Gate mode at or above threshold; the caller applies it to its own note view.
    /// </summary>
    public sealed class BoardNoteTriage
    {
        /// <summary>The note id this triage belongs to.</summary>
        public required string Id { get; init; }

        /// <summary>The attention label (see <see cref="InboxItem.Attention"/> values).</summary>
        public string? Attention { get; init; }

        /// <summary>The note kind: handoff, status, question, stop_sign, or hold_notice.</summary>
        public string? NoteKind { get; init; }
    }
}
