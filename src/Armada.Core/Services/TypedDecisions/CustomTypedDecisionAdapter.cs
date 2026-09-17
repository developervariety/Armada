namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Runs a user-defined custom typed decision. Unlike the built-in adapters, a custom decision has
    /// no deterministic rule and no orchestration seam of its own: it assembles its state from a
    /// supplied context, asks the operator's questions, records one event, and — only on the
    /// MissionDiff surface and only when bound and gated at or above threshold — raises an advisory
    /// flag. It never lands, dispatches, approves a PASS, holds a rescue, or writes memory. The
    /// binding action list is fixed in <see cref="CustomDecisionSeamEnum"/> and every member is
    /// non-approving by construction.
    ///
    /// The skeleton mirrors <see cref="TypedDecisionAdapterBase{TInput,TVerdict,TModel}"/>: Off makes
    /// no call, an unavailable provider records nothing acted, Shadow or below-threshold records the
    /// answer without acting, and Gate at or above threshold records and may raise the advisory flag.
    /// It never throws into a caller.
    /// </summary>
    public sealed class CustomTypedDecisionAdapter
    {
        #region Public-Members

        /// <summary>Event type for a custom decision that gated at or above its threshold and flagged.</summary>
        public const string EventTypeFlagged = "typed_decision.custom_flagged";

        /// <summary>Event type for a custom decision call that recorded its answer without acting.</summary>
        public const string EventTypeRecorded = "typed_decision.custom";

        #endregion

        #region Private-Members

        private const string _Header = "[CustomTypedDecisionAdapter] ";

        private readonly ITypedDecisionClient _Client;
        private readonly TypedDecisionRecorder _Recorder;
        private readonly TypedDecisionSettings _Settings;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Create the adapter.</summary>
        /// <param name="client">The typed-decision client; never called when the decision is Off.</param>
        /// <param name="recorder">The recorder that writes one event per consulted call.</param>
        /// <param name="settings">Typed-decision settings; the effective mode and threshold come from here.</param>
        /// <param name="logging">Logging module.</param>
        public CustomTypedDecisionAdapter(ITypedDecisionClient client, TypedDecisionRecorder recorder, TypedDecisionSettings settings, LoggingModule logging)
        {
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            _Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the request a custom decision would send for a context, without calling the provider.
        /// Returns null when the decision does not exist or has no questions. The eval store uses this
        /// to replay a custom decision exactly as production would.
        /// </summary>
        /// <param name="name">Custom decision name.</param>
        /// <param name="context">The field values to assemble the state from, keyed by field name.</param>
        /// <returns>The redacted state and questions, or null.</returns>
        public TypedDecisionBatchItem? DescribeRequest(string name, IReadOnlyDictionary<string, object?> context)
        {
            if (!_Settings.Custom.TryGetValue(name, out CustomTypedDecisionSettings? definition) || definition == null) return null;
            return Prepare(definition, context);
        }

        /// <summary>
        /// Run a custom decision over a context. Returns the model answers and whether the decision
        /// flagged. Never throws; an Off decision, a missing decision, or an unavailable provider
        /// returns a result that flagged nothing.
        /// </summary>
        /// <param name="name">Custom decision name.</param>
        /// <param name="context">The field values to assemble the state from.</param>
        /// <param name="mission">The mission the call is about, for event attribution; may be null.</param>
        /// <param name="captainId">The captain that invoked it, for attribution; may be null.</param>
        /// <param name="token">Cancellation token, forwarded to the client.</param>
        /// <returns>The outcome; never null.</returns>
        public async Task<CustomDecisionOutcome> RunAsync(
            string name,
            IReadOnlyDictionary<string, object?> context,
            Mission? mission,
            string? captainId,
            CancellationToken token)
        {
            if (!_Settings.Custom.TryGetValue(name, out CustomTypedDecisionSettings? definition) || definition == null)
                return CustomDecisionOutcome.NotFound(name);

            ResolvedTypedDecision cfg = _Settings.ForCustom(name);
            if (cfg.Mode == TypedDecisionModeEnum.Off)
                return CustomDecisionOutcome.Inactive(name);

            TypedDecisionBatchItem? item = Prepare(definition, context);
            if (item == null) return CustomDecisionOutcome.Inactive(name);

            TypedDecisionResult result;
            try
            {
                result = await _Client.DecideAsync(new TypedDecisionRequest
                {
                    DecisionPoint = "custom:" + name,
                    State = item.State.State,
                    Questions = item.Questions
                }, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "custom decision '" + name + "' client threw, no action: " + ex.Message);
                result = TypedDecisionResult.Exception();
            }

            TypedDecisionEventContext eventContext = new TypedDecisionEventContext
            {
                DecisionPoint = "custom:" + name,
                RuleVerdict = "none",
                ModelVerdict = Summarize(result),
                Confidence = HighestConfidence(result),
                Result = result,
                RedactedState = item.State.Text,
                Mission = mission,
                CaptainId = captainId
            };

            if (!result.Available)
            {
                await _Recorder.RecordUnavailableAsync(eventContext, token).ConfigureAwait(false);
                return CustomDecisionOutcome.Unavailable(name, result);
            }

            double confidence = HighestConfidence(result) ?? 0.0;
            bool gated = cfg.Mode == TypedDecisionModeEnum.Gate
                && definition.Binding != CustomDecisionSeamEnum.None
                && confidence >= cfg.GateThreshold
                && cfg.GateThreshold > 0.0;

            if (gated)
            {
                await _Recorder.RecordGatedAsync(eventContext, token).ConfigureAwait(false);
                _Logging.Info(_Header + "custom decision '" + name + "' flagged at " + confidence.ToString("0.00"));
                return CustomDecisionOutcome.Flagged(name, result, confidence);
            }

            string outcome = cfg.Mode == TypedDecisionModeEnum.Shadow ? "shadow_mode" : "below_threshold";
            await _Recorder.RecordShadowAsync(eventContext, outcome, token).ConfigureAwait(false);
            return CustomDecisionOutcome.Recorded(name, result, confidence);
        }

        #endregion

        #region Private-Methods

        private TypedDecisionBatchItem? Prepare(CustomTypedDecisionSettings definition, IReadOnlyDictionary<string, object?> context)
        {
            Dictionary<string, TypedQuestion> questions = new Dictionary<string, TypedQuestion>(StringComparer.Ordinal);
            foreach (CustomTypedQuestionSettings question in definition.Questions)
            {
                if (String.IsNullOrWhiteSpace(question.Id) || String.IsNullOrWhiteSpace(question.Instructions)) continue;
                questions[question.Id] = ToQuestion(question);
            }
            if (questions.Count == 0) return null;

            object state = BuildState(definition, context);
            RedactedDecisionState redacted = DecisionStateRedactor.RedactState(state, _Settings.MaxStateChars);
            return new TypedDecisionBatchItem(redacted, questions);
        }

        private static object BuildState(CustomTypedDecisionSettings definition, IReadOnlyDictionary<string, object?> context)
        {
            // Assemble only the fields the definition names, in order, from the supplied context. On
            // the MissionDiff surface StateFields selects mission fields; on the captain tool the
            // caller supplies whatever the questions need. Unknown or absent fields are skipped.
            Dictionary<string, object?> state = new Dictionary<string, object?>(StringComparer.Ordinal);
            IReadOnlyList<string> fields = definition.StateFields.Count > 0
                ? definition.StateFields
                : new List<string> { "diff", "output_tail" };
            foreach (string field in fields)
            {
                if (context.TryGetValue(field, out object? value) && value != null) state[field] = value;
            }
            // A captain-tool decision may pass the whole context through when it names no fields it
            // recognises, so the questions still have something to read.
            if (state.Count == 0)
                foreach (KeyValuePair<string, object?> pair in context)
                    if (pair.Value != null) state[pair.Key] = pair.Value;
            return state;
        }

        private static TypedQuestion ToQuestion(CustomTypedQuestionSettings question)
        {
            switch ((question.Type ?? "noul").Trim().ToLowerInvariant())
            {
                case "choice":
                    return new ChoiceQuestion(question.Instructions, question.Options);
                case "score":
                    return new ScoreQuestion(question.Instructions, question.Levels);
                default:
                    return new NoulQuestion(question.Instructions, question.TrueMeaning, question.FalseMeaning);
            }
        }

        private static string Summarize(TypedDecisionResult result)
        {
            if (!result.Available) return result.UnavailableReason ?? "unavailable";
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, TypedAnswer> entry in result.Answers)
            {
                TypedAnswer answer = entry.Value;
                string value = answer.Choice
                    ?? (answer.Noul.HasValue ? answer.Noul.Value.ToString("0.00") : answer.Score.HasValue ? answer.Score.Value.ToString("0.0") : "?");
                parts.Add(entry.Key + "=" + value);
            }
            return String.Join(" ", parts);
        }

        private static double? HighestConfidence(TypedDecisionResult result)
        {
            if (!result.Available) return null;
            double? best = null;
            foreach (TypedAnswer answer in result.Answers.Values)
            {
                double? confidence = answer.Confidence
                    ?? (answer.Noul.HasValue ? Math.Abs(answer.Noul.Value - 0.5) * 2.0 : (double?)null);
                if (confidence.HasValue && (!best.HasValue || confidence.Value > best.Value)) best = confidence.Value;
            }
            return best;
        }

        #endregion
    }

    /// <summary>The outcome of running a custom decision.</summary>
    public sealed class CustomDecisionOutcome
    {
        /// <summary>The decision name.</summary>
        public string Name { get; init; } = String.Empty;

        /// <summary>The status: <c>flagged</c>, <c>recorded</c>, <c>unavailable</c>, <c>inactive</c>, or <c>not_found</c>.</summary>
        public string Status { get; init; } = "inactive";

        /// <summary>The model answers, when the provider answered.</summary>
        public TypedDecisionResult? Result { get; init; }

        /// <summary>The highest answer confidence, when answered.</summary>
        public double? Confidence { get; init; }

        /// <summary>Whether the decision raised its advisory flag.</summary>
        public bool DidFlag => Status == "flagged";

        internal static CustomDecisionOutcome NotFound(string name) => new CustomDecisionOutcome { Name = name, Status = "not_found" };
        internal static CustomDecisionOutcome Inactive(string name) => new CustomDecisionOutcome { Name = name, Status = "inactive" };
        internal static CustomDecisionOutcome Unavailable(string name, TypedDecisionResult r) => new CustomDecisionOutcome { Name = name, Status = "unavailable", Result = r };
        internal static CustomDecisionOutcome Recorded(string name, TypedDecisionResult r, double c) => new CustomDecisionOutcome { Name = name, Status = "recorded", Result = r, Confidence = c };
        internal static CustomDecisionOutcome Flagged(string name, TypedDecisionResult r, double c) => new CustomDecisionOutcome { Name = name, Status = "flagged", Result = r, Confidence = c };
    }
}
