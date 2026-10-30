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
    /// The event and its retained sample also carry the decision's subject: the objective id and the
    /// mission id, whichever the caller knows. They are record links for joining a call to the
    /// objective or mission it judged. They are never part of the transmitted state, so they change
    /// neither the state hash nor what leaves the host.
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
        /// Event type for a recorded call that made no change: the rule's verdict stands. Its
        /// <c>gate_outcome</c> says why: <c>shadow_mode</c> for a decision in Shadow mode, and
        /// <c>below_threshold</c> or <c>advisory_only</c> for a Gate-mode call that took no action.
        /// </summary>
        public const string EventTypeRuleKept = "typed_decision.rule_kept";

        /// <summary>
        /// Earlier name of <see cref="EventTypeRuleKept"/>. Events written before the rename keep it, so
        /// every reader that selects typed-decision calls by type accepts both names.
        /// </summary>
        public const string EventTypeRuleKeptLegacy = "typed_decision.shadow";

        /// <summary><c>subject_type</c> for a call that judged one mission.</summary>
        public const string SubjectTypeMission = "mission";

        /// <summary><c>subject_type</c> for a call that judged one objective.</summary>
        public const string SubjectTypeObjective = "objective";

        /// <summary><c>subject_type</c> for a call that judged one papercut group.</summary>
        public const string SubjectTypePapercutGroup = "papercut_group";

        /// <summary><c>subject_type</c> for a call that judged one merge entry with no mission.</summary>
        public const string SubjectTypeMergeEntry = "merge_entry";

        /// <summary><c>subject_unavailable_reason</c> when the caller named no record the call judged.</summary>
        public const string SubjectUnavailableReason = "caller_named_no_subject";

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

        /// <summary>
        /// Event type for the outcome a gated decision's action reached: the choice a person made on what
        /// the decision held, or a timeout that ended the hold without one. It names the decision event and
        /// carries its <c>state_sha256</c>, so it joins to the decision and its retained state.
        /// </summary>
        public const string EventTypeLabelled = "typed_decision.labelled";

        /// <summary>
        /// Event type for a decision answered from stored answers to the same state, question contract and
        /// model, with no new provider call. It records no tokens and is not a typed-decision call.
        /// </summary>
        public const string EventTypeCacheHit = "typed_decision.cache_hit";

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
        /// Record a call that made no change: a Shadow-mode call, or a Gate-mode call that took no
        /// action. Emits <see cref="EventTypeRuleKept"/>. The
        /// <paramref name="gateOutcome"/> distinguishes the two cases (for example <c>shadow_mode</c>
        /// or <c>below_threshold</c>).
        /// </summary>
        /// <param name="context">The decision context; its state is hashed, never stored.</param>
        /// <param name="gateOutcome">The reason no change was made.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created event, or null when recording failed.</returns>
        public Task<ArmadaEvent?> RecordRuleKeptAsync(TypedDecisionEventContext context, string gateOutcome, CancellationToken token)
        {
            string outcome = String.IsNullOrWhiteSpace(gateOutcome) ? "shadow" : gateOutcome;
            return RecordAsync(EventTypeRuleKept, outcome, context, token);
        }

        /// <summary>
        /// Record a decision answered from stored answers instead of a new provider call. Emits
        /// <see cref="EventTypeCacheHit"/> with the outcome the stored answers produced under the current
        /// mode and threshold. The event carries the stored answers and model and no tokens, so its
        /// estimated cost is zero when the model has a price.
        /// </summary>
        /// <param name="context">The decision context, carrying the stored result; its state is hashed, never stored.</param>
        /// <param name="gateOutcome">The outcome the stored answers produced.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created event, or null when recording failed.</returns>
        public Task<ArmadaEvent?> RecordCacheHitAsync(TypedDecisionEventContext context, string gateOutcome, CancellationToken token)
        {
            if (context == null) return Task.FromResult<ArmadaEvent?>(null);
            TypedDecisionResult stored = context.Result;
            TypedDecisionEventContext hit = new TypedDecisionEventContext
            {
                DecisionPoint = context.DecisionPoint,
                RuleVerdict = context.RuleVerdict,
                ModelVerdict = context.ModelVerdict,
                Confidence = context.Confidence,
                Result = new TypedDecisionResult
                {
                    Available = stored?.Available ?? false,
                    Provenance = stored?.Provenance,
                    UnavailableReason = stored?.UnavailableReason,
                    UnavailableDetail = stored?.UnavailableDetail,
                    Answers = stored?.Answers ?? new Dictionary<string, TypedAnswer>(),
                    Model = stored?.Model,
                    InputTokens = 0,
                    OutputTokens = 0,
                    LatencyMs = 0,
                    BatchSize = 1
                },
                RedactedState = context.RedactedState,
                Mission = context.Mission,
                MissionId = context.MissionId,
                ObjectiveId = context.ObjectiveId,
                CaptainId = context.CaptainId,
                CallerIdentity = context.CallerIdentity,
                ToolName = context.ToolName
            };
            string outcome = String.IsNullOrWhiteSpace(gateOutcome) ? "cache_hit" : gateOutcome;
            return RecordAsync(EventTypeCacheHit, outcome, hit, token);
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
                ["state_sha256"] = stateSha256,
                ["objective_id"] = ReadPayloadString(original.Payload, "objective_id"),
                ["mission_id"] = original.MissionId
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
                    ObjectiveId = ReadPayloadString(original.Payload, "objective_id"),
                    MissionId = original.MissionId,
                    RedactorVersion = DecisionStateRedactor.Version
                }, token).ConfigureAwait(false);
            }

            return TypedDecisionReversalResult.Recorded(recorded, decisionPoint, labelled);
        }

        /// <summary>
        /// Record the outcome a gated decision's action reached. Writes one <see cref="EventTypeLabelled"/>
        /// event that names the decision event and copies its <c>state_sha256</c>, objective id, rule verdict
        /// and gate outcome, so the label joins to the decision and its retained state. When the decision
        /// event cannot be read the label is still written, with a null digest and the reason named. It
        /// changes no decision, gate or other record. Never throws.
        /// </summary>
        /// <param name="decisionPoint">The decision the label belongs to.</param>
        /// <param name="decisionEventId">The gated decision event, when known.</param>
        /// <param name="label">The outcome label, in the decision's own vocabulary.</param>
        /// <param name="decidedBy">Who reached the outcome: an operator name, or a fixed value for an automatic outcome.</param>
        /// <param name="reason">Why, in the decider's words; may be empty.</param>
        /// <param name="mission">The mission the decision judged.</param>
        /// <param name="details">Further label fields; must carry no state and no credential.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created event, or null when recording failed.</returns>
        public async Task<ArmadaEvent?> RecordOutcomeLabelAsync(
            string decisionPoint,
            string? decisionEventId,
            string label,
            string decidedBy,
            string? reason,
            Mission? mission,
            IReadOnlyDictionary<string, object?>? details,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(decisionPoint) || String.IsNullOrWhiteSpace(label)) return null;

            ArmadaEvent? original = null;
            string? lookupFailure = null;
            if (String.IsNullOrWhiteSpace(decisionEventId) || !decisionEventId!.StartsWith("evt_", StringComparison.Ordinal))
            {
                lookupFailure = "no decision event id was recorded";
            }
            else
            {
                try
                {
                    original = await _Database.Events.ReadAsync(decisionEventId!, token).ConfigureAwait(false);
                    if (original == null) lookupFailure = "the decision event was not found";
                    else if (!IsTypedDecisionEvent(original.EventType))
                    {
                        lookupFailure = "event " + decisionEventId + " is not a typed-decision event";
                        original = null;
                    }
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "label lookup failed for " + decisionEventId + ": " + ex.Message);
                    lookupFailure = "the decision event could not be read";
                }
            }

            Dictionary<string, object?> payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["decision"] = decisionPoint,
                ["original_event_id"] = original?.Id ?? (String.IsNullOrWhiteSpace(decisionEventId) ? null : decisionEventId),
                ["original_event_type"] = original?.EventType,
                ["rule_verdict"] = original != null ? ReadPayloadString(original.Payload, "rule_verdict") : null,
                ["gate_outcome"] = original != null ? ReadPayloadString(original.Payload, "gate_outcome") : null,
                ["state_sha256"] = original != null ? ReadPayloadString(original.Payload, "state_sha256") : null,
                ["objective_id"] = original != null ? ReadPayloadString(original.Payload, "objective_id") : null,
                ["mission_id"] = mission?.Id ?? original?.MissionId,
                ["label"] = label,
                ["decided_by"] = decidedBy ?? String.Empty,
                ["reason"] = reason ?? String.Empty,
                ["join_unavailable_reason"] = lookupFailure
            };
            if (details != null)
            {
                foreach (KeyValuePair<string, object?> entry in details)
                {
                    if (String.IsNullOrWhiteSpace(entry.Key) || payload.ContainsKey(entry.Key)) continue;
                    payload[entry.Key] = entry.Value;
                }
            }

            try
            {
                ArmadaEvent evt = new ArmadaEvent(EventTypeLabelled, "decision=" + decisionPoint + " label=" + label + " by=" + (decidedBy ?? String.Empty))
                {
                    Payload = JsonSerializer.Serialize(payload)
                };
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
                    evt.TenantId = original?.TenantId ?? Constants.DefaultTenantId;
                    evt.UserId = original?.UserId;
                    evt.EntityType = original?.EntityType;
                    evt.EntityId = original?.EntityId;
                    evt.MissionId = original?.MissionId;
                    evt.VesselId = original?.VesselId;
                    evt.VoyageId = original?.VoyageId;
                }

                return await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to record " + EventTypeLabelled + ": " + ex.Message);
                return null;
            }
        }

        #endregion

        #region Public-Static-Methods

        /// <summary>
        /// Whether an event type names one typed-decision call: gated, rule kept (under its current or
        /// earlier name), unavailable, or a captain-tool call.
        /// </summary>
        /// <param name="eventType">The event type.</param>
        /// <returns>True for a typed-decision call event.</returns>
        public static bool IsDecisionCallEventType(string? eventType)
        {
            return String.Equals(eventType, EventTypeGated, StringComparison.Ordinal)
                || IsRuleKeptEventType(eventType)
                || String.Equals(eventType, EventTypeUnavailable, StringComparison.Ordinal)
                || String.Equals(eventType, EventTypeCaptain, StringComparison.Ordinal);
        }

        /// <summary>
        /// Whether an event type names a call that kept the rule, under its current or earlier name.
        /// </summary>
        /// <param name="eventType">The event type.</param>
        /// <returns>True for a rule-kept event.</returns>
        public static bool IsRuleKeptEventType(string? eventType)
        {
            return String.Equals(eventType, EventTypeRuleKept, StringComparison.Ordinal)
                || String.Equals(eventType, EventTypeRuleKeptLegacy, StringComparison.Ordinal);
        }

        #endregion

        #region Private-Methods

        private static bool IsTypedDecisionEvent(string? eventType)
        {
            return IsDecisionCallEventType(eventType);
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
                    Payload = BuildPayload(context, gateOutcome, _Settings?.Invoke()?.Pricing)
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
                    string? missionId = MissionIdOf(context);
                    if (missionId != null)
                    {
                        evt.EntityType = "mission";
                        evt.EntityId = missionId;
                        evt.MissionId = missionId;
                    }
                }

                if (!String.IsNullOrWhiteSpace(context.CaptainId)) evt.CaptainId = context.CaptainId;

                ArmadaEvent created = await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
                // A cache hit repeats a state already retained by the call it reuses.
                if (!String.Equals(eventType, EventTypeCacheHit, StringComparison.Ordinal))
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
                ObjectiveId = ObjectiveIdOf(context),
                MissionId = MissionIdOf(context),
                RedactorVersion = DecisionStateRedactor.Version
            }, token).ConfigureAwait(false);
        }

        /// <summary>The mission the call judged: the mission record's id, else the id the caller supplied.</summary>
        private static string? MissionIdOf(TypedDecisionEventContext context)
        {
            if (!String.IsNullOrWhiteSpace(context.Mission?.Id)) return context.Mission!.Id;
            return String.IsNullOrWhiteSpace(context.MissionId) ? null : context.MissionId!.Trim();
        }

        /// <summary>The objective the call judged, when the caller knows it.</summary>
        private static string? ObjectiveIdOf(TypedDecisionEventContext context)
        {
            return String.IsNullOrWhiteSpace(context.ObjectiveId) ? null : context.ObjectiveId!.Trim();
        }

        /// <summary>
        /// The record the call judged, for the outcome join: the subject the caller names, else the mission,
        /// else the objective. Both halves are null when the caller knows none of them.
        /// </summary>
        private static TypedDecisionSubject SubjectOf(TypedDecisionEventContext context)
        {
            if (!String.IsNullOrWhiteSpace(context.SubjectType) && !String.IsNullOrWhiteSpace(context.SubjectId))
                return new TypedDecisionSubject(context.SubjectType!.Trim(), context.SubjectId!.Trim());
            string? missionId = MissionIdOf(context);
            if (missionId != null) return new TypedDecisionSubject(SubjectTypeMission, missionId);
            string? objectiveId = ObjectiveIdOf(context);
            if (objectiveId != null) return new TypedDecisionSubject(SubjectTypeObjective, objectiveId);
            return new TypedDecisionSubject(null, null);
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

        private static string BuildPayload(TypedDecisionEventContext context, string gateOutcome, TypedDecisionPriceTable? pricing)
        {
            TypedDecisionResult? result = context.Result;
            TypedDecisionCostEstimate cost = TypedDecisionCostEstimator.Estimate(pricing, result);
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
                ["model_verdict"] = context.ModelVerdict,
                ["confidence"] = context.Confidence,
                ["answers"] = answers,
                ["confidences"] = confidences,
                ["input_tokens"] = result?.InputTokens ?? 0,
                ["output_tokens"] = result?.OutputTokens ?? 0,
                ["latency_ms"] = result?.LatencyMs ?? 0,
                ["batch_size"] = result?.BatchSize ?? 1,
                ["estimated_cost_usd"] = cost.EstimatedCostUsd,
                ["cost_is_estimate"] = true,
                ["cost_unavailable_reason"] = cost.UnavailableReason,
                ["price_table_version"] = cost.PriceTableVersion,
                ["state_sha256"] = stateSha256,
                ["provenance_unavailable_reason"] = result?.Provenance?.UnavailableReason
                    ?? (result?.Available == true && result.Provenance == null ? "not_recorded" : null),
                ["questions_sha256"] = result?.Provenance?.QuestionsSha256,
                ["wire_questions_sha256"] = result?.Provenance?.WireQuestionsSha256,
                ["request_sha256"] = result?.Provenance?.RequestSha256,
                ["batch_item_index"] = result?.Provenance?.BatchItemIndex,
                ["state_bytes"] = stateBytes,
                ["gate_outcome"] = gateOutcome,
                ["unavailable_reason"] = result?.UnavailableReason,
                ["unavailable_detail"] = result?.UnavailableDetail,
                ["objective_id"] = ObjectiveIdOf(context),
                ["mission_id"] = MissionIdOf(context),
                ["rule_answers"] = context.RuleAnswers
            };

            TypedDecisionSubject subject = SubjectOf(context);
            payload["subject_type"] = subject.Type;
            payload["subject_id"] = subject.Id;
            if (subject.Type == null) payload["subject_unavailable_reason"] = SubjectUnavailableReason;

            if (!String.IsNullOrWhiteSpace(context.CallerIdentity)) payload["caller_identity"] = context.CallerIdentity;
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
        /// The id of the mission this decision judged, for a caller that holds the id but not the record.
        /// Ignored when <see cref="Mission"/> is set. A record link only: it is never part of the state.
        /// </summary>
        public string? MissionId { get; init; }

        /// <summary>
        /// The id of the objective this decision judged, when the caller knows it. A record link only: it
        /// is never part of the state, so it changes neither the state hash nor what leaves the host.
        /// </summary>
        public string? ObjectiveId { get; init; }

        /// <summary>
        /// The captain that made the call, when known. Set on captain-tool events for attribution.
        /// </summary>
        public string? CaptainId { get; init; }

        /// <summary>
        /// The authenticated caller's identity label, when supplied. This is attribution only,
        /// not a captain id or an authorization credential.
        /// </summary>
        public string? CallerIdentity { get; init; }

        /// <summary>The tool that requested this decision, when called through a tool.</summary>
        public string? ToolName { get; init; }

        /// <summary>
        /// The kind of record this call judged when it is not one mission or one objective, for example
        /// <c>papercut_group</c> or <c>merge_entry</c>. Set together with <see cref="SubjectId"/>. A record link
        /// only: it is never part of the state.
        /// </summary>
        public string? SubjectType { get; init; }

        /// <summary>The id of the record named by <see cref="SubjectType"/>.</summary>
        public string? SubjectId { get; init; }

        /// <summary>
        /// The deterministic rule's answer to each question the model was asked, keyed by question id, so
        /// rule-versus-model agreement is computable per question. A question the rule has no answer for maps
        /// to null; a decision that supplies no per-question answers leaves this null.
        /// </summary>
        public IReadOnlyDictionary<string, object?>? RuleAnswers { get; init; }
    }

    /// <summary>
    /// The record one typed-decision call judged: its kind and id, or both null when the caller knew none.
    /// </summary>
    public sealed class TypedDecisionSubject
    {
        /// <summary>The subject kind, for example <c>mission</c>, <c>objective</c> or <c>papercut_group</c>.</summary>
        public string? Type { get; }

        /// <summary>The subject id.</summary>
        public string? Id { get; }

        /// <summary>Create a subject.</summary>
        /// <param name="type">The subject kind.</param>
        /// <param name="id">The subject id.</param>
        public TypedDecisionSubject(string? type, string? id)
        {
            Type = type;
            Id = id;
        }
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
