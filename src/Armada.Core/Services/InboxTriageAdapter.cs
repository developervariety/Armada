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
    /// The D11 <c>inbox_triage</c> decision adapter. It scores inbox items for how urgently a human
    /// is needed, and — only in Gate mode, only at or above the decision threshold — annotates each
    /// with an <c>attention</c> label and re-orders the response by attention.
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
        /// Upper bound on items scored per inbox call. The inbox is interactive, so triage is bounded; the
        /// tail beyond the budget keeps its deterministic position with no attention label.
        /// </summary>
        public const int MaxItemsPerCall = 40;

        #endregion

        #region Private-Members

        private const string _Header = "[InboxTriageAdapter] ";
        private const string _AttentionQuestionId = "needs_human_now";

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
                outcomes = await ScoreAttentionAsync(scored.Select(BuildInboxState).ToList(), scored.Select(MissionIdOf).ToList(), scored.Select(SubjectOf).ToList(),
                    scored.Select(item => (IEnumerable<string?>)(item.VesselIds ?? new List<string>())).ToList(), "severity_order", cfg, token).ConfigureAwait(false);
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

        #endregion

        #region Private-Methods

        private async Task<List<AttentionOutcome>> ScoreAttentionAsync(
            List<object> rawStates,
            List<string?> missionIds,
            List<TypedDecisionSubject?> subjects,
            List<IEnumerable<string?>> vesselIds,
            string ruleVerdict,
            ResolvedTypedDecision cfg,
            CancellationToken token)
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal)
            {
                [_AttentionQuestionId] = new ScoreQuestion(
                    "A human needs to act on this item now. Place the urgency on the scale below.",
                    _AttentionLevels)
            };

            // Items are independent, so they are scored together in as few requests as the limits allow;
            // each item still gets its own recorded event.
            // The shared egress guard, per item: an item about an excluded vessel, or naming an excluded marker, is
            // not sent; it keeps its deterministic place and records why.
            List<string?> refusals = rawStates
                .Select((raw, index) => TypedDecisionEgress.Refusal(_Settings, DecisionPoint, vesselIds[index], () => raw))
                .ToList();
            List<TypedDecisionBatchItem?> batch = rawStates
                .Select((raw, index) => refusals[index] != null ? null : new TypedDecisionBatchItem(DecisionStateRedactor.RedactState(raw, _Settings.MaxStateChars), questions))
                .ToList();
            List<TypedDecisionResult> results = await TypedDecisionEgress.DecideAllowedAsync(
                _Client, DecisionPoint, batch, refusals, _Settings.MaxStateChars, token).ConfigureAwait(false);

            List<AttentionOutcome> outcomes = new List<AttentionOutcome>(results.Count);
            for (int index = 0; index < results.Count; index++)
            {
                TypedDecisionResult result = results[index];
                string redacted = batch[index]?.State.Text ?? String.Empty;
                string? missionId = missionIds[index];
                TypedDecisionSubject? subject = subjects[index];

                if (TypedDecisionEgress.IsRefusal(result))
                {
                    await SafeRecordAsync(() => _Recorder.RecordUnavailableAsync(
                        BuildContext(ruleVerdict, null, null, result, redacted, missionId, subject), token)).ConfigureAwait(false);
                    outcomes.Add(new AttentionOutcome { Available = true, Applied = false });
                    continue;
                }

                if (result == null || !result.Available)
                {
                    // Record the unavailable call once; the caller stops at the first unavailable item.
                    await SafeRecordAsync(() => _Recorder.RecordUnavailableAsync(
                        BuildContext(ruleVerdict, null, null, result ?? TypedDecisionResult.Exception(), redacted, missionId, subject), token)).ConfigureAwait(false);
                    outcomes.Add(new AttentionOutcome { Available = false });
                    return outcomes;
                }

                string attention = InterpretAttention(result, out double confidence);
                bool apply = cfg.Mode == TypedDecisionModeEnum.Gate && confidence >= cfg.GateThreshold;
                string verdict = attention;

                if (apply)
                {
                    await SafeRecordAsync(() => _Recorder.RecordGatedAsync(
                        BuildContext(ruleVerdict, verdict, confidence, result, redacted, missionId, subject), token)).ConfigureAwait(false);
                }
                else
                {
                    string outcome = cfg.Mode == TypedDecisionModeEnum.Shadow ? "shadow_mode" : "below_threshold";
                    await SafeRecordAsync(() => _Recorder.RecordRuleKeptAsync(
                        BuildContext(ruleVerdict, verdict, confidence, result, redacted, missionId, subject), outcome, token)).ConfigureAwait(false);
                }

                outcomes.Add(new AttentionOutcome { Available = true, Applied = apply, Attention = attention });
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

        // An item about a mission records the mission; an item about another record (an incident, an
        // objective, a check) records that record; an item about no record has no subject.
        private static TypedDecisionSubject? SubjectOf(InboxItem item)
        {
            if (String.IsNullOrWhiteSpace(item.EntityType) || String.IsNullOrWhiteSpace(item.EntityId)) return null;
            if (String.Equals(item.EntityType, "mission", StringComparison.OrdinalIgnoreCase)) return null;
            return new TypedDecisionSubject(item.EntityType.Trim().ToLowerInvariant(), item.EntityId.Trim());
        }

        private static string? MissionIdOf(InboxItem item)
        {
            if (!String.Equals(item.EntityType, "mission", StringComparison.OrdinalIgnoreCase)) return null;
            return String.IsNullOrWhiteSpace(item.EntityId) ? null : item.EntityId;
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

        private static int AttentionRank(string? attention)
        {
            if (String.IsNullOrEmpty(attention)) return -1;
            for (int i = 0; i < _AttentionLevels.Count; i++)
                if (String.Equals(_AttentionLevels[i], attention, StringComparison.Ordinal)) return i;
            return -1;
        }

        private Task SafeRecordAsync(Func<Task> record)
        {
            return TypedDecisionRecording.SafeRecordAsync(record, _Logging, _Header);
        }

        private static TypedDecisionEventContext BuildContext(
            string ruleVerdict,
            string? modelVerdict,
            double? confidence,
            TypedDecisionResult result,
            string redactedState,
            string? missionId,
            TypedDecisionSubject? subject)
        {
            return new TypedDecisionEventContext
            {
                SubjectType = subject?.Type,
                SubjectId = subject?.Id,
                DecisionPoint = DecisionPoint,
                RuleVerdict = ruleVerdict,
                ModelVerdict = modelVerdict,
                Confidence = confidence,
                Result = result,
                RedactedState = redactedState,
                MissionId = missionId
            };
        }

        #endregion

        #region Private-Types

        private sealed class AttentionOutcome
        {
            public bool Available { get; init; }
            public bool Applied { get; init; }
            public string? Attention { get; init; }
        }

        private sealed class InboxAttentionAssignment
        {
            public required InboxItem Item { get; init; }
            public required string Attention { get; init; }
        }

        #endregion
    }
}
