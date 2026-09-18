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
    /// One reading of a typed-decision result: the model's short verdict label and the single
    /// confidence the generic gate compares against the decision's threshold. Each decision
    /// interprets its own answers into this shape; the compound, rule-dependent detail lives on the
    /// concrete reading and is consulted only in <c>Combine</c>.
    /// </summary>
    public abstract class TypedModelReading
    {
        /// <summary>
        /// The confidence of the single action the model proposes, in [0, 1]. The generic adapter
        /// gate records a shadow and keeps the rule whenever this is below the decision threshold, so
        /// a reading that proposes no action reports zero.
        /// </summary>
        public abstract double Confidence { get; }

        /// <summary>
        /// A short label for the model's verdict, used on the recorded event message.
        /// </summary>
        public abstract string Label { get; }
    }

    /// <summary>
    /// The shared skeleton every gated typed-decision adapter follows. A decision point holds an
    /// adapter, never the raw client, and calls <see cref="DecideAsync"/> with the input and the
    /// deterministic rule verdict. The skeleton is fixed and identical for every decision:
    /// <list type="bullet">
    /// <item>Off — return the rule, no call.</item>
    /// <item>unavailable — return the rule, record <c>typed_decision.unavailable</c>.</item>
    /// <item>Shadow, or Gate below threshold — return the rule, record <c>typed_decision.shadow</c>.</item>
    /// <item>Gate at or above threshold — <c>Combine</c> the rule and the model, where rule
    /// hard-blocks always win, and record <c>typed_decision.gated</c>.</item>
    /// </list>
    /// The adapter never throws into a caller and never converts a rule hard-block into a less
    /// conservative outcome: the model may only hold, escalate, or flag a case the rule would have
    /// allowed.
    /// </summary>
    /// <typeparam name="TInput">The decision input the adapter builds state from.</typeparam>
    /// <typeparam name="TVerdict">The verdict type the seam consumes.</typeparam>
    /// <typeparam name="TModel">The concrete reading of the model result.</typeparam>
    public abstract class TypedDecisionAdapterBase<TInput, TVerdict, TModel>
        where TModel : TypedModelReading
    {
        #region Private-Members

        private readonly ITypedDecisionClient _Client;
        private readonly TypedDecisionRecorder _Recorder;
        private readonly TypedDecisionSettings _Settings;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a typed-decision adapter.
        /// </summary>
        /// <param name="client">The injected typed-decision client. Never called when the decision is Off.</param>
        /// <param name="recorder">The recorder that writes one event per consulted call.</param>
        /// <param name="settings">Typed-decision settings; the effective mode and threshold come from here.</param>
        /// <param name="logging">Logging module.</param>
        protected TypedDecisionAdapterBase(
            ITypedDecisionClient client,
            TypedDecisionRecorder recorder,
            TypedDecisionSettings settings,
            LoggingModule logging)
        {
            _Client = client ?? throw new ArgumentNullException(nameof(client));
            _Recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Consult the model for this decision and return the effective verdict. The deterministic
        /// <paramref name="ruleVerdict"/> is returned unchanged in every case except a gate at or
        /// above threshold, and even then only <c>Combine</c> may narrow it. Never throws.
        /// </summary>
        /// <param name="input">The decision input.</param>
        /// <param name="ruleVerdict">The deterministic rule verdict, always the fallback.</param>
        /// <param name="token">Cancellation token, forwarded to the client so its timeout links to the caller.</param>
        /// <returns>The rule verdict, or the combined verdict when the model gated at or above threshold.</returns>
        public async Task<TVerdict> DecideAsync(TInput input, TVerdict ruleVerdict, CancellationToken token)
        {
            ResolvedTypedDecision cfg = _Settings.For(DecisionPoint);
            if (cfg.Mode == TypedDecisionModeEnum.Off) return ruleVerdict;

            // Checked before the state is even built: nothing about a mission on an excluded vessel is
            // serialized, let alone sent. The rule stands and the refusal is recorded, never silent.
            if (!AllowsEgress(input))
            {
                await RecordUnavailableAsync(input, ruleVerdict, EgressExcluded(), String.Empty, token).ConfigureAwait(false);
                return ruleVerdict;
            }

            TypedDecisionBatchItem? item = Prepare(input);
            if (item == null) return ruleVerdict;

            TypedDecisionResult result;
            try
            {
                // The caller's token is forwarded unchanged so the client links its settings timeout to
                // it: a slow decision cancels through this same token and returns unavailable, not late.
                result = await _Client.DecideAsync(new TypedDecisionRequest
                {
                    DecisionPoint = DecisionPoint,
                    State = item.State.State,
                    Questions = item.Questions
                }, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The client is contracted never to throw into a caller; guard anyway so a decision is
                // never able to break recovery, refusal handling, or runtime classification.
                _Logging.Warn(_Header + "decision '" + DecisionPoint + "' client threw, rule stands: " + ex.Message);
                result = TypedDecisionResult.Exception();
            }

            return await CompleteAsync(input, ruleVerdict, result, item.State.Text, cfg, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Consult the model for several independent inputs at once and return each effective verdict,
        /// in order. The inputs are answered in as few provider requests as the limits allow; each input
        /// is then gated and recorded exactly as <see cref="DecideAsync"/> would. Use this only when no
        /// input's outcome affects another's. An input whose state cannot be built keeps its rule. When
        /// the provider is unavailable, the first affected input records the unavailable event and every
        /// later input keeps its rule without a further event. Never throws.
        /// </summary>
        /// <param name="inputs">The decision inputs.</param>
        /// <param name="ruleVerdicts">The deterministic rule verdict for each input, in the same order.</param>
        /// <param name="token">Cancellation token, forwarded to the client.</param>
        /// <returns>One verdict per input, in order.</returns>
        public async Task<List<TVerdict>> DecideManyAsync(IReadOnlyList<TInput> inputs, IReadOnlyList<TVerdict> ruleVerdicts, CancellationToken token)
        {
            if (inputs == null) throw new ArgumentNullException(nameof(inputs));
            if (ruleVerdicts == null) throw new ArgumentNullException(nameof(ruleVerdicts));
            if (inputs.Count != ruleVerdicts.Count) throw new ArgumentException("Each input needs one rule verdict.", nameof(ruleVerdicts));

            List<TVerdict> verdicts = new List<TVerdict>(ruleVerdicts);
            ResolvedTypedDecision cfg = _Settings.For(DecisionPoint);
            if (cfg.Mode == TypedDecisionModeEnum.Off || inputs.Count == 0) return verdicts;

            List<int> positions = new List<int>();
            List<TypedDecisionBatchItem> batch = new List<TypedDecisionBatchItem>();
            for (int index = 0; index < inputs.Count; index++)
            {
                if (!AllowsEgress(inputs[index]))
                {
                    await RecordUnavailableAsync(inputs[index], ruleVerdicts[index], EgressExcluded(), String.Empty, token).ConfigureAwait(false);
                    continue;
                }

                TypedDecisionBatchItem? item = Prepare(inputs[index]);
                if (item == null) continue;
                positions.Add(index);
                batch.Add(item);
            }
            if (batch.Count == 0) return verdicts;

            List<TypedDecisionResult> results = await TypedDecisionBatcher.DecideAllAsync(
                _Client, DecisionPoint, batch, _Settings.MaxStateChars, token).ConfigureAwait(false);

            for (int slot = 0; slot < results.Count; slot++)
            {
                int index = positions[slot];
                verdicts[index] = await CompleteAsync(inputs[index], ruleVerdicts[index], results[slot], batch[slot].State.Text, cfg, token).ConfigureAwait(false);
                if (!results[slot].Available) break;
            }

            return verdicts;
        }

        #endregion

        #region Protected-Members

        /// <summary>The recorder, for a decision that records a domain event beside its decision event.</summary>
        protected TypedDecisionRecorder Recorder => _Recorder;

        /// <summary>The logging module.</summary>
        protected LoggingModule Logging => _Logging;

        /// <summary>
        /// Observe an available model reading before the gate compares it to the threshold. A decision
        /// overrides this to report a side signal (an event, a board note, a papercut) that is
        /// informative only. It runs for every available reading while the decision is not Off, so an
        /// override checks <paramref name="cfg"/> itself. It can never change the returned verdict, and
        /// an exception it throws is logged and ignored.
        /// </summary>
        /// <param name="input">The decision input.</param>
        /// <param name="model">The interpreted reading.</param>
        /// <param name="cfg">The effective mode and threshold.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that completes when the observation has been attempted.</returns>
        protected virtual Task OnModelReadingAsync(TInput input, TModel model, ResolvedTypedDecision cfg, CancellationToken token)
        {
            return Task.CompletedTask;
        }

        #endregion

        #region Protected-Abstract-Members

        /// <summary>The decision-point name; a row in the settings decisions map.</summary>
        protected abstract string DecisionPoint { get; }

        /// <summary>A short header for log lines.</summary>
        protected abstract string _Header { get; }

        /// <summary>Build the decision state object from the input, before redaction.</summary>
        protected abstract object BuildState(TInput input);

        /// <summary>The typed questions this decision asks, keyed by question id.</summary>
        protected abstract IReadOnlyDictionary<string, TypedQuestion> BuildQuestions();

        /// <summary>
        /// The typed questions this decision asks about one input. Override when the question set depends
        /// on the input (for example one question per listed item); the default asks
        /// <see cref="BuildQuestions()"/>.
        /// </summary>
        /// <param name="input">The decision input.</param>
        /// <returns>The questions, keyed by question id. Empty means the decision is not consulted.</returns>
        protected virtual IReadOnlyDictionary<string, TypedQuestion> BuildQuestions(TInput input)
        {
            return BuildQuestions();
        }

        /// <summary>Interpret an available result into this decision's reading.</summary>
        protected abstract TModel Interpret(TypedDecisionResult result);

        /// <summary>
        /// Combine the rule verdict with the model reading when the model gated at or above threshold.
        /// Rule hard-blocks always win: this only ever makes the outcome more conservative, and never
        /// converts a rule hard-block into a less conservative one.
        /// </summary>
        protected abstract TVerdict Combine(TVerdict ruleVerdict, TModel model);

        /// <summary>A short label for the rule verdict, used on the recorded event message.</summary>
        protected abstract string RuleLabel(TVerdict ruleVerdict);

        /// <summary>The mission this decision belongs to, when any, for the event owner scope.</summary>
        protected abstract Mission? MissionOf(TInput input);

        #endregion

        #region Private-Methods

        /// <summary>
        /// Build the exact redacted state and questions this decision would send for an input, without
        /// calling the provider or recording anything. The evaluation set uses it so a case tests the
        /// questions production asks. Returns null when the state or questions cannot be built.
        /// </summary>
        /// <param name="input">The decision input.</param>
        /// <returns>The request item, or null.</returns>
        public TypedDecisionBatchItem? DescribeRequest(TInput input)
        {
            return Prepare(input);
        }

        /// <summary>The unavailable reason a vessel egress exclusion records.</summary>
        public const string EgressExcludedReason = "egress_excluded_vessel";

        private bool AllowsEgress(TInput input)
        {
            Mission? mission;
            try
            {
                mission = MissionOf(input);
            }
            catch (Exception)
            {
                mission = null;
            }
            return _Settings.AllowsEgress(mission?.VesselId);
        }

        private static TypedDecisionResult EgressExcluded()
        {
            return new TypedDecisionResult { Available = false, UnavailableReason = EgressExcludedReason };
        }

        private TypedDecisionBatchItem? Prepare(TInput input)
        {
            try
            {
                IReadOnlyDictionary<string, TypedQuestion> questions = BuildQuestions(input);

                // Nothing to ask means nothing to send: the rule stands and no event is recorded.
                if (questions == null || questions.Count == 0) return null;

                return new TypedDecisionBatchItem(
                    DecisionStateRedactor.RedactState(BuildState(input), _Settings.MaxStateChars),
                    questions);
            }
            catch (Exception ex)
            {
                // Building state or questions must never throw into the caller; keep the rule.
                _Logging.Warn(_Header + "decision '" + DecisionPoint + "' state build failed, rule stands: " + ex.Message);
                return null;
            }
        }

        private async Task<TVerdict> CompleteAsync(TInput input, TVerdict ruleVerdict, TypedDecisionResult? result, string redacted, ResolvedTypedDecision cfg, CancellationToken token)
        {
            if (result == null || !result.Available)
            {
                await RecordUnavailableAsync(input, ruleVerdict, result ?? TypedDecisionResult.Exception(), redacted, token).ConfigureAwait(false);
                return ruleVerdict;
            }

            TModel model;
            try
            {
                model = Interpret(result);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "decision '" + DecisionPoint + "' interpretation failed, rule stands: " + ex.Message);
                return ruleVerdict;
            }

            try
            {
                await OnModelReadingAsync(input, model, cfg, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // An observation is informative only; its failure must never change the verdict.
                _Logging.Warn(_Header + "decision '" + DecisionPoint + "' reading observer failed, rule stands: " + ex.Message);
            }

            if (cfg.Mode == TypedDecisionModeEnum.Shadow || model.Confidence < cfg.GateThreshold)
            {
                string outcome = cfg.Mode == TypedDecisionModeEnum.Shadow ? "shadow_mode" : "below_threshold";
                await RecordShadowAsync(input, ruleVerdict, model, result, outcome, redacted, token).ConfigureAwait(false);
                return ruleVerdict;
            }

            TVerdict gated = Combine(ruleVerdict, model);
            await RecordGatedAsync(input, ruleVerdict, model, result, redacted, token).ConfigureAwait(false);
            return gated;
        }

        private Task RecordUnavailableAsync(TInput input, TVerdict ruleVerdict, TypedDecisionResult result, string redacted, CancellationToken token)
        {
            return SafeRecordAsync(() => _Recorder.RecordUnavailableAsync(
                BuildContext(input, ruleVerdict, null, result, redacted), token));
        }

        private Task RecordShadowAsync(TInput input, TVerdict ruleVerdict, TModel model, TypedDecisionResult result, string outcome, string redacted, CancellationToken token)
        {
            return SafeRecordAsync(() => _Recorder.RecordShadowAsync(
                BuildContext(input, ruleVerdict, model, result, redacted), outcome, token));
        }

        private Task RecordGatedAsync(TInput input, TVerdict ruleVerdict, TModel model, TypedDecisionResult result, string redacted, CancellationToken token)
        {
            return SafeRecordAsync(() => _Recorder.RecordGatedAsync(
                BuildContext(input, ruleVerdict, model, result, redacted), token));
        }

        private Task SafeRecordAsync(Func<Task> record)
        {
            return TypedDecisionRecording.SafeRecordAsync(record, _Logging, _Header + "decision '" + DecisionPoint + "' ");
        }

        private TypedDecisionEventContext BuildContext(TInput input, TVerdict ruleVerdict, TModel? model, TypedDecisionResult result, string redacted)
        {
            return new TypedDecisionEventContext
            {
                DecisionPoint = DecisionPoint,
                RuleVerdict = RuleLabel(ruleVerdict),
                ModelVerdict = model?.Label,
                Confidence = model?.Confidence,
                Result = result,
                RedactedState = redacted,
                Mission = MissionOf(input)
            };
        }

        #endregion
    }
}
