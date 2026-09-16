namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Records one <see cref="ArmadaEvent"/> per typed-decision call for observability. The event
    /// carries the decision, the deterministic rule verdict, the model's typed answers and
    /// confidences, token and latency counts, the gate outcome, and a hash and byte count of the
    /// transmitted state. It NEVER carries the state itself — only <c>state_sha256</c> and
    /// <c>state_bytes</c>.
    /// </summary>
    public sealed class TypedDecisionRecorder
    {
        #region Private-Members

        private const string _Header = "[TypedDecisionRecorder] ";

        /// <summary>Event type for a gate at or above threshold that changed the outcome.</summary>
        public const string EventTypeGated = "typed_decision.gated";

        /// <summary>
        /// Event type for a recorded call that made no change: a Shadow-mode call, or a Gate-mode
        /// call whose confidence was below the threshold. The rule stands.
        /// </summary>
        public const string EventTypeShadow = "typed_decision.shadow";

        /// <summary>Event type for a call whose model answer was unavailable.</summary>
        public const string EventTypeUnavailable = "typed_decision.unavailable";

        /// <summary>
        /// Event type for a call the captain-facing tool made directly. One is written per captain
        /// tool call, whatever the outcome: a delivered answer, a disabled or dormant tool, an
        /// exhausted per-mission budget, or an unavailable provider. It carries the same state hash
        /// and byte count as every other typed-decision event and never the state itself.
        /// </summary>
        public const string EventTypeCaptain = "typed_decision.captain";

        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a typed-decision recorder.
        /// </summary>
        /// <param name="database">Database driver whose Events collection receives the record.</param>
        /// <param name="logging">Logging module.</param>
        public TypedDecisionRecorder(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record a call whose gated outcome changed the rule verdict. Emits
        /// <see cref="EventTypeGated"/> with <c>gate_outcome</c> <c>applied</c>.
        /// </summary>
        /// <param name="context">The decision context; its state is hashed, never stored.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created event, or null when recording failed (recording never throws).</returns>
        public Task<ArmadaEvent?> RecordGatedAsync(TypedDecisionEventContext context, CancellationToken token)
        {
            return RecordAsync(EventTypeGated, "applied", context, token);
        }

        /// <summary>
        /// Record a call that made no change: a Shadow-mode call, or a Gate-mode call whose
        /// confidence was below the threshold. Emits <see cref="EventTypeShadow"/>. The
        /// <paramref name="gateOutcome"/> distinguishes the two cases (for example <c>shadow_mode</c>
        /// or <c>below_threshold</c>).
        /// </summary>
        /// <param name="context">The decision context; its state is hashed, never stored.</param>
        /// <param name="gateOutcome">The reason no change was made.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created event, or null when recording failed.</returns>
        public Task<ArmadaEvent?> RecordShadowAsync(TypedDecisionEventContext context, string gateOutcome, CancellationToken token)
        {
            string outcome = String.IsNullOrWhiteSpace(gateOutcome) ? "shadow" : gateOutcome;
            return RecordAsync(EventTypeShadow, outcome, context, token);
        }

        /// <summary>
        /// Record a call whose model answer was unavailable. Emits <see cref="EventTypeUnavailable"/>
        /// with <c>gate_outcome</c> <c>unavailable</c>.
        /// </summary>
        /// <param name="context">The decision context; its state is hashed, never stored.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created event, or null when recording failed.</returns>
        public Task<ArmadaEvent?> RecordUnavailableAsync(TypedDecisionEventContext context, CancellationToken token)
        {
            return RecordAsync(EventTypeUnavailable, "unavailable", context, token);
        }

        /// <summary>
        /// Record a call the captain-facing tool made directly. Emits <see cref="EventTypeCaptain"/>
        /// with the supplied <paramref name="outcome"/> as <c>gate_outcome</c> (for example
        /// <c>delivered</c>, <c>disabled</c>, <c>dormant</c>, <c>budget_exhausted</c>, or
        /// <c>unavailable</c>). The captain tool takes no deterministic rule and applies no gate, so
        /// the event exists for observability only; it changes no Armada record.
        /// </summary>
        /// <param name="context">The decision context; its state is hashed, never stored.</param>
        /// <param name="outcome">The captain-call outcome label.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created event, or null when recording failed.</returns>
        public Task<ArmadaEvent?> RecordCaptainAsync(TypedDecisionEventContext context, string outcome, CancellationToken token)
        {
            string gateOutcome = String.IsNullOrWhiteSpace(outcome) ? "captain" : outcome;
            return RecordAsync(EventTypeCaptain, gateOutcome, context, token);
        }

        #endregion

        #region Private-Methods

        private async Task<ArmadaEvent?> RecordAsync(string eventType, string gateOutcome, TypedDecisionEventContext context, CancellationToken token)
        {
            if (context == null) return null;

            try
            {
                ArmadaEvent evt = new ArmadaEvent(eventType, BuildMessage(eventType, context))
                {
                    Payload = BuildPayload(context, gateOutcome)
                };

                if (context.Mission != null)
                {
                    evt.EntityType = "mission";
                    evt.EntityId = context.Mission.Id;
                    evt.MissionId = context.Mission.Id;
                    evt.VesselId = context.Mission.VesselId;
                    evt.VoyageId = context.Mission.VoyageId;
                    EventOwnerScope.ApplyFromMission(evt, context.Mission);
                }
                else
                {
                    evt.TenantId = Constants.DefaultTenantId;
                }

                if (!String.IsNullOrWhiteSpace(context.CaptainId)) evt.CaptainId = context.CaptainId;

                return await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to record " + eventType + ": " + ex.Message);
                return null;
            }
        }

        private static string BuildMessage(string eventType, TypedDecisionEventContext context)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("decision=").Append(context.DecisionPoint);
            sb.Append(" rule=").Append(String.IsNullOrWhiteSpace(context.RuleVerdict) ? "none" : context.RuleVerdict);

            if (String.Equals(eventType, EventTypeUnavailable, StringComparison.Ordinal)
                || (context.Result != null && !context.Result.Available))
            {
                sb.Append(" unavailable=").Append(context.Result?.UnavailableReason ?? "unknown");
                return sb.ToString();
            }

            if (!String.IsNullOrWhiteSpace(context.ModelVerdict))
                sb.Append(" model=").Append(context.ModelVerdict);
            if (context.Confidence.HasValue)
                sb.Append(" conf=").Append(context.Confidence.Value.ToString("0.00", CultureInfo.InvariantCulture));

            return sb.ToString();
        }

        private static string BuildPayload(TypedDecisionEventContext context, string gateOutcome)
        {
            TypedDecisionResult? result = context.Result;
            string stateSha256 = ComputeSha256(context.RedactedState);
            int stateBytes = String.IsNullOrEmpty(context.RedactedState) ? 0 : Encoding.UTF8.GetByteCount(context.RedactedState);

            Dictionary<string, object?> answers = new Dictionary<string, object?>(StringComparer.Ordinal);
            Dictionary<string, object?> confidences = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (result?.Answers != null)
            {
                foreach (KeyValuePair<string, TypedAnswer> entry in result.Answers)
                {
                    TypedAnswer answer = entry.Value;
                    if (answer == null) continue;
                    answers[entry.Key] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = answer.Type,
                        ["choice"] = answer.Choice,
                        ["score"] = answer.Score,
                        ["noul"] = answer.Noul,
                        ["confidence"] = answer.Confidence
                    };
                    confidences[entry.Key] = answer.Confidence;
                }
            }

            Dictionary<string, object?> payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["decision"] = context.DecisionPoint,
                ["rule_verdict"] = context.RuleVerdict,
                ["answers"] = answers,
                ["confidences"] = confidences,
                ["input_tokens"] = result?.InputTokens ?? 0,
                ["output_tokens"] = result?.OutputTokens ?? 0,
                ["latency_ms"] = result?.LatencyMs ?? 0,
                ["state_sha256"] = stateSha256,
                ["state_bytes"] = stateBytes,
                ["gate_outcome"] = gateOutcome,
                ["unavailable_reason"] = result?.UnavailableReason
            };

            return JsonSerializer.Serialize(payload);
        }

        private static string ComputeSha256(string? text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? String.Empty);
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        #endregion
    }

    /// <summary>
    /// The context recorded for one typed-decision call. Carries the redacted state only so the
    /// recorder can hash it; the recorder never stores the state.
    /// </summary>
    public sealed class TypedDecisionEventContext
    {
        /// <summary>
        /// The decision-point name.
        /// </summary>
        public required string DecisionPoint { get; init; }

        /// <summary>
        /// The deterministic rule verdict, as a short label.
        /// </summary>
        public required string RuleVerdict { get; init; }

        /// <summary>
        /// The model's short verdict label, when available.
        /// </summary>
        public string? ModelVerdict { get; init; }

        /// <summary>
        /// The model's confidence for the message line, when available.
        /// </summary>
        public double? Confidence { get; init; }

        /// <summary>
        /// The typed-decision result. Supplies answers, confidences, tokens, latency, and the
        /// unavailable reason.
        /// </summary>
        public required TypedDecisionResult Result { get; init; }

        /// <summary>
        /// The exact redacted state that was transmitted. Hashed for <c>state_sha256</c> and
        /// measured for <c>state_bytes</c>; never stored on the event.
        /// </summary>
        public string RedactedState { get; init; } = "";

        /// <summary>
        /// The mission this decision belongs to, when any. Supplies the event's owner scope and ids.
        /// </summary>
        public Mission? Mission { get; init; }

        /// <summary>
        /// The captain that made the call, when known. Set on captain-tool events for attribution.
        /// </summary>
        public string? CaptainId { get; init; }
    }
}
