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
    using Armada.Core.Services.TypedDecisions;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Records one <see cref="ArmadaEvent"/> per typed-decision call for observability. The event
    /// carries the decision, the deterministic rule verdict, the model's typed answers and
    /// confidences, token and latency counts, the gate outcome, and a hash and byte count of the
    /// transmitted state. It NEVER carries the state itself — only <c>state_sha256</c> and
    /// <c>state_bytes</c>.
    ///
    /// When a sample store is supplied and the decision opts in, the same call also appends its
    /// REDACTED state to the host-local training store (<see cref="TypedDecisionSampleStore"/>).
    /// That store is the only place the text is kept, it never leaves the host, and a retention
    /// failure never changes a decision's outcome.
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
        /// tool call, whatever the outcome: a delivered answer, a disabled or dormant tool,
        /// or an unavailable provider. It carries the same state hash
        /// and byte count as every other typed-decision event and never the state itself.
        /// </summary>
        public const string EventTypeCaptain = "typed_decision.captain";

        /// <summary>Event type for an operator reversing a gated outcome.</summary>
        public const string EventTypeReversed = "typed_decision.reversed";

        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private readonly TypedDecisionSampleStore? _Samples;
        private readonly Func<TypedDecisionSettings?>? _Settings;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a typed-decision recorder.
        /// </summary>
        /// <param name="database">Database driver whose Events collection receives the record.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="samples">Optional host-local sample store for retained state; null retains nothing.</param>
        /// <param name="settings">Optional accessor for live typed-decision settings, read on each call so a
        /// retention change takes effect without a restart; null retains nothing.</param>
        public TypedDecisionRecorder(
            DatabaseDriver database,
            LoggingModule logging,
            TypedDecisionSampleStore? samples = null,
            Func<TypedDecisionSettings?>? settings = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Samples = samples;
            _Settings = settings;
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
        /// <c>delivered</c>, <c>disabled</c>, <c>dormant</c>, or
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

        /// <summary>
        /// Record a domain event a decision raised beside its decision event (for example a suspected
        /// provider account fault). The caller supplies a message and payload that carry no state and
        /// no credential; the event is owner-scoped from the mission when one is given. Never throws.
        /// </summary>
        /// <param name="eventType">The domain event type.</param>
        /// <param name="message">The event message.</param>
        /// <param name="payload">The JSON payload.</param>
        /// <param name="mission">The related mission, when any.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created event, or null when recording failed.</returns>
        public async Task<ArmadaEvent?> RecordDomainEventAsync(string eventType, string message, string payload, Mission? mission, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(eventType)) return null;

            try
            {
                ArmadaEvent evt = new ArmadaEvent(eventType, message ?? String.Empty) { Payload = payload };
                if (mission != null)
                {
                    evt.EntityType = "mission";
                    evt.EntityId = mission.Id;
                    evt.MissionId = mission.Id;
                    evt.VesselId = mission.VesselId;
                    evt.VoyageId = mission.VoyageId;
                    EventOwnerScope.ApplyFromMission(evt, mission);
                }
                else
                {
                    evt.TenantId = Constants.DefaultTenantId;
                }

                return await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to record " + eventType + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Record an operator reversing a gated outcome: the decision acted, and a person judged the
        /// result wrong. Writes a <c>typed_decision.reversed</c> event naming the original event, and
        /// appends a label to the sample store when the decision retains state. This is the labelled
        /// example a local classifier is trained and reviewed against, so it is recorded by the
        /// platform rather than left to a hand-written note. Never throws.
        /// </summary>
        /// <param name="eventId">The typed-decision event being reversed.</param>
        /// <param name="correctedVerdict">The answer the operator says was correct.</param>
        /// <param name="reason">Why the gated outcome was wrong.</param>
        /// <param name="reversedBy">Who reversed it.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The reversal outcome: the created event, or the reason none was created.</returns>
        public async Task<TypedDecisionReversalResult> RecordReversalAsync(
            string eventId,
            string correctedVerdict,
            string reason,
            string reversedBy,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(eventId))
                return TypedDecisionReversalResult.Refused("an event id is required");
            if (String.IsNullOrWhiteSpace(correctedVerdict))
                return TypedDecisionReversalResult.Refused("a corrected verdict is required");

            ArmadaEvent? original;
            try
            {
                original = await _Database.Events.ReadAsync(eventId, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "reversal lookup failed for " + eventId + ": " + ex.Message);
                return TypedDecisionReversalResult.Refused("the event could not be read");
            }

            if (original == null) return TypedDecisionReversalResult.Refused("no event with that id");
            if (!IsTypedDecisionEvent(original.EventType))
                return TypedDecisionReversalResult.Refused("event " + eventId + " is not a typed-decision event");

            string decisionPoint = ReadPayloadString(original.Payload, "decision") ?? "unknown";
            string stateSha256 = ReadPayloadString(original.Payload, "state_sha256") ?? String.Empty;

            Dictionary<string, object?> payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["decision"] = decisionPoint,
                ["original_event_id"] = original.Id,
                ["original_event_type"] = original.EventType,
                ["rule_verdict"] = ReadPayloadString(original.Payload, "rule_verdict"),
                ["gate_outcome"] = ReadPayloadString(original.Payload, "gate_outcome"),
                ["corrected_verdict"] = correctedVerdict,
                ["reason"] = reason ?? String.Empty,
                ["reversed_by"] = reversedBy ?? String.Empty,
                ["state_sha256"] = stateSha256
            };

            ArmadaEvent? recorded;
            try
            {
                ArmadaEvent evt = new ArmadaEvent(EventTypeReversed,
                    "decision=" + decisionPoint + " reversed to=" + correctedVerdict)
                {
                    Payload = JsonSerializer.Serialize(payload),
                    TenantId = original.TenantId ?? Constants.DefaultTenantId,
                    UserId = original.UserId,
                    EntityType = original.EntityType,
                    EntityId = original.EntityId,
                    MissionId = original.MissionId,
                    VesselId = original.VesselId,
                    VoyageId = original.VoyageId,
                    CaptainId = original.CaptainId
                };
                recorded = await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to record " + EventTypeReversed + ": " + ex.Message);
                return TypedDecisionReversalResult.Refused("the reversal event could not be written");
            }

            bool labelled = false;
            if (_Samples != null && _Settings != null && TypedDecisionSampleStore.Retains(_Settings(), decisionPoint))
            {
                labelled = await _Samples.AppendAsync(new TypedDecisionSample
                {
                    Kind = TypedDecisionSampleStore.KindReversal,
                    DecisionPoint = decisionPoint,
                    EventId = original.Id,
                    StateSha256 = stateSha256,
                    RuleVerdict = ReadPayloadString(original.Payload, "rule_verdict"),
                    GateOutcome = ReadPayloadString(original.Payload, "gate_outcome"),
                    CorrectedVerdict = correctedVerdict,
                    Reason = reason,
                    MissionId = original.MissionId,
                    RedactorVersion = DecisionStateRedactor.Version
                }, token).ConfigureAwait(false);
            }

            return TypedDecisionReversalResult.Recorded(recorded, decisionPoint, labelled);
        }

        #endregion

        #region Private-Methods

        private static bool IsTypedDecisionEvent(string? eventType)
        {
            return String.Equals(eventType, EventTypeGated, StringComparison.Ordinal)
                || String.Equals(eventType, EventTypeShadow, StringComparison.Ordinal)
                || String.Equals(eventType, EventTypeUnavailable, StringComparison.Ordinal)
                || String.Equals(eventType, EventTypeCaptain, StringComparison.Ordinal);
        }

        private static string? ReadPayloadString(string? payload, string property)
        {
            if (String.IsNullOrWhiteSpace(payload)) return null;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(payload))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                    if (!document.RootElement.TryGetProperty(property, out JsonElement value)) return null;
                    return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

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

                ArmadaEvent created = await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
                await RetainAsync(context, gateOutcome, created, token).ConfigureAwait(false);
                return created;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to record " + eventType + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Append the call's redacted state to the host-local store when the decision opts in. Best
        /// effort: a failure is logged and swallowed, exactly like a recorder failure.
        /// </summary>
        private async Task RetainAsync(TypedDecisionEventContext context, string gateOutcome, ArmadaEvent? evt, CancellationToken token)
        {
            if (_Samples == null || _Settings == null) return;
            TypedDecisionSettings? settings = _Settings();
            if (!TypedDecisionSampleStore.Retains(settings, context.DecisionPoint)) return;

            await _Samples.AppendAsync(new TypedDecisionSample
            {
                Kind = TypedDecisionSampleStore.KindDecision,
                DecisionPoint = context.DecisionPoint,
                EventId = evt?.Id,
                RedactedState = context.RedactedState ?? String.Empty,
                StateSha256 = ComputeSha256(context.RedactedState),
                Provenance = context.Result.Provenance,
                Model = context.Result.Model,
                Answers = context.Result.Answers,
                BatchSize = context.Result.BatchSize,
                RuleVerdict = context.RuleVerdict,
                ModelVerdict = context.ModelVerdict,
                Confidence = context.Confidence,
                GateOutcome = gateOutcome,
                MissionId = context.Mission?.Id,
                RedactorVersion = DecisionStateRedactor.Version
            }, token).ConfigureAwait(false);
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
                        ["confidence"] = answer.Confidence,
                        ["probabilities"] = answer.Probabilities
                    };
                    confidences[entry.Key] = answer.Confidence;
                }
            }

            Dictionary<string, object?> payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["decision"] = context.DecisionPoint,
                ["rule_verdict"] = context.RuleVerdict,
                ["model"] = result?.Model,
                ["answers"] = answers,
                ["confidences"] = confidences,
                ["input_tokens"] = result?.InputTokens ?? 0,
                ["output_tokens"] = result?.OutputTokens ?? 0,
                ["latency_ms"] = result?.LatencyMs ?? 0,
                ["batch_size"] = result?.BatchSize ?? 1,
                ["state_sha256"] = stateSha256,
                ["questions_sha256"] = result?.Provenance?.QuestionsSha256,
                ["wire_questions_sha256"] = result?.Provenance?.WireQuestionsSha256,
                ["request_sha256"] = result?.Provenance?.RequestSha256,
                ["batch_item_index"] = result?.Provenance?.BatchItemIndex,
                ["state_bytes"] = stateBytes,
                ["gate_outcome"] = gateOutcome,
                ["unavailable_reason"] = result?.UnavailableReason,
                ["unavailable_detail"] = result?.UnavailableDetail
            };

            if (!String.IsNullOrWhiteSpace(context.ParticipantKey)) payload["participant_key"] = context.ParticipantKey;
            if (!String.IsNullOrWhiteSpace(context.ToolName)) payload["tool_name"] = context.ToolName;
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

        /// <summary>
        /// The calling session's participant key, when supplied. This is attribution only,
        /// not a captain id or an authorization credential.
        /// </summary>
        public string? ParticipantKey { get; init; }

        /// <summary>The tool that requested this decision, when called through a tool.</summary>
        public string? ToolName { get; init; }
    }

    /// <summary>
    /// The outcome of recording an operator reversal: the event written, or why none was.
    /// </summary>
    public sealed class TypedDecisionReversalResult
    {
        /// <summary>Whether the reversal was recorded.</summary>
        public bool Success { get; init; }

        /// <summary>Why the reversal was refused, when it was.</summary>
        public string? Refusal { get; init; }

        /// <summary>The reversal event, when one was written.</summary>
        public ArmadaEvent? Event { get; init; }

        /// <summary>The decision point the reversed call belonged to.</summary>
        public string? DecisionPoint { get; init; }

        /// <summary>Whether a labelled sample was appended to the training store.</summary>
        public bool Labelled { get; init; }

        /// <summary>A refused reversal, with its reason.</summary>
        /// <param name="reason">Why the reversal was refused.</param>
        /// <returns>The refusal.</returns>
        public static TypedDecisionReversalResult Refused(string reason)
        {
            return new TypedDecisionReversalResult { Success = false, Refusal = reason };
        }

        /// <summary>A recorded reversal.</summary>
        /// <param name="evt">The reversal event.</param>
        /// <param name="decisionPoint">The decision point.</param>
        /// <param name="labelled">Whether a labelled sample was appended.</param>
        /// <returns>The result.</returns>
        public static TypedDecisionReversalResult Recorded(ArmadaEvent? evt, string decisionPoint, bool labelled)
        {
            return new TypedDecisionReversalResult { Success = true, Event = evt, DecisionPoint = decisionPoint, Labelled = labelled };
        }
    }
}
