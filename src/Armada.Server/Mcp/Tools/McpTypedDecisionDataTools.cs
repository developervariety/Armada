namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.TypedDecisions;
    using Armada.Core.Settings;
    using Armada.Server.Mcp;
    using SyslogLogging;

    /// <summary>
    /// Operator-facing tools over the typed-decision training data: record that a gated outcome was
    /// wrong, and report what has been retained per decision. Operator-scoped, so a mission captain
    /// cannot reach them. Neither tool changes a decision, a gate, or any other Armada record: one
    /// writes a reversal event (plus its label in the host-local store), the other only counts.
    /// </summary>
    public static class McpTypedDecisionDataTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>Registered name of the reversal tool.</summary>
        public const string ReversalToolName = "armada_typed_decision_reversal";

        /// <summary>Registered name of the retained-data report.</summary>
        public const string LabelsToolName = "armada_typed_decision_labels";

        /// <summary>Registered name of the bounded retained preflight sample tool.</summary>
        public const string SampleToolName = "armada_typed_decision_sample";

        /// <summary>
        /// Register the operator tools over typed-decision data.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="database">Database used to verify and scope the event before reading retained state.</param>
        /// <param name="recorder">The typed-decision recorder that writes the reversal event.</param>
        /// <param name="samples">The host-local sample store, or null when retention is not configured.</param>
        /// <param name="settings">Live typed-decision settings, read per call.</param>
        /// <param name="logging">Optional logging module.</param>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            TypedDecisionRecorder? recorder,
            TypedDecisionSampleStore? samples,
            Func<TypedDecisionSettings?> settings,
            LoggingModule? logging)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            register(
                ReversalToolName,
                "Record that a gated typed decision was wrong. Give the decision event id in 'eventId', the answer that was correct in 'correctedVerdict', and why in 'reason'. Writes one typed_decision.reversed event naming the original, and, when that decision retains state, a labelled example in the host-local training store. It reverses nothing by itself: the work you already corrected stands as you left it, and no gate, threshold, or record changes. Operator tool.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        eventId = new { type = "string", description = "The typed-decision event being reversed (evt_ prefix)." },
                        correctedVerdict = new { type = "string", description = "The answer that was correct, in the decision's own vocabulary." },
                        reason = new { type = "string", description = "Why the gated outcome was wrong, in one or two sentences." },
                        reversedBy = new { type = "string", description = "Who reversed it; defaults to 'operator'." }
                    },
                    required = new[] { "eventId", "correctedVerdict" }
                },
                async (JsonElement? args) =>
                {
                    if (recorder == null) return Error("The typed-decision recorder is not configured.");
                    if (args == null || args.Value.ValueKind != JsonValueKind.Object)
                        return Error("The call carried no arguments object.");

                    ReversalRequest? request;
                    try
                    {
                        request = JsonSerializer.Deserialize<ReversalRequest>(args.Value, _JsonOptions);
                    }
                    catch (JsonException ex)
                    {
                        return Error("The arguments are not valid: " + ex.Message);
                    }

                    if (request == null || String.IsNullOrWhiteSpace(request.EventId))
                        return Error("An eventId is required.");
                    if (String.IsNullOrWhiteSpace(request.CorrectedVerdict))
                        return Error("A correctedVerdict is required.");

                    TypedDecisionReversalResult result = await recorder.RecordReversalAsync(
                        request.EventId,
                        request.CorrectedVerdict,
                        request.Reason ?? String.Empty,
                        String.IsNullOrWhiteSpace(request.ReversedBy) ? "operator" : request.ReversedBy!).ConfigureAwait(false);

                    if (!result.Success)
                    {
                        logging?.Warn("[McpTypedDecisionDataTools] reversal refused: " + result.Refusal);
                        return Error(result.Refusal ?? "The reversal was refused.");
                    }

                    return new
                    {
                        Success = true,
                        EventId = result.Event?.Id,
                        OriginalEventId = request.EventId,
                        Decision = result.DecisionPoint,
                        Labelled = result.Labelled,
                        Message = result.Labelled
                            ? "Reversal recorded and kept as a labelled example."
                            : "Reversal recorded. This decision does not retain state, so no labelled example was kept."
                    };
                });

            register(
                LabelsToolName,
                "Report the typed-decision training data retained on this host: per decision, how many calls and operator reversals are stored, and whether that is enough to train on. A decision below the minimum is named with the reason, never left out. Reading only; nothing leaves the host. Operator tool.",
                new
                {
                    type = "object",
                    properties = new { },
                    required = Array.Empty<string>()
                },
                (JsonElement? args) =>
                {
                    TypedDecisionSettings? live = settings();
                    if (live == null) return Task.FromResult<object>(Error("Typed-decision settings are not available."));

                    if (samples == null || !live.Retention.Enabled)
                    {
                        return Task.FromResult<object>(new
                        {
                            Success = true,
                            RetentionEnabled = false,
                            Decisions = Array.Empty<object>(),
                            Message = "Retention is off, so nothing is being kept. Enable typedDecisions.retention and set retainState on each decision that should contribute."
                        });
                    }

                    List<TypedDecisionSampleCount> counts = samples.Summarize(live.Retention.MinimumSamplesPerDecision);
                    List<string> optedIn = TypedDecisionSampleStore.OptedInDecisionPoints(live);

                    return Task.FromResult<object>(new
                    {
                        Success = true,
                        RetentionEnabled = true,
                        live.Retention.RetentionDays,
                        MinimumSamples = live.Retention.MinimumSamplesPerDecision,
                        OptedInDecisions = optedIn,
                        Decisions = counts,
                        Message = counts.Count == 0
                            ? "Retention is on, but nothing is stored yet."
                            : counts.Count + " decision(s) have retained data."
                    });
                });

            register(
                SampleToolName,
                "Read one retained redacted preflight input by typed-decision event id. Returns bounded pages and digest provenance only to a global operator. It never returns request provenance, model answers, mission or objective ownership fields, or model rationale. A specific Q4 premise is reported as not recorded when no explicit premise reference is stored. Reading only; no gate or preparation state changes. Operator tool.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        eventId = new { type = "string", description = "The typed-decision event id (evt_ prefix)." },
                        offset = new { type = "integer", minimum = 0, description = "Character offset in the retained redacted state; defaults to zero." },
                        maxChars = new { type = "integer", minimum = 1, maximum = 4096, description = "Maximum page length; defaults to 1024 and is capped at 4096." }
                    },
                    required = new[] { "eventId" }
                },
                async (JsonElement? args) =>
                {
                    AuthContext? caller = McpCallerContext.Current;
                    if (caller == null || !caller.IsAuthenticated || caller.IsAdmin == false
                        || !String.IsNullOrEmpty(caller.MissionId))
                        return SampleError("forbidden");

                    if (args == null) return SampleError("invalid_request");

                    SampleRequest? request;
                    try
                    {
                        request = JsonSerializer.Deserialize<SampleRequest>(args.Value, _JsonOptions);
                    }
                    catch (JsonException)
                    {
                        return SampleError("invalid_request");
                    }

                    if (request == null || String.IsNullOrWhiteSpace(request.EventId)
                        || request.Offset < 0 || request.MaxChars < 1)
                        return SampleError("invalid_request");

                    TypedDecisionSettings? live = settings();
                    if (samples == null || live == null) return SampleError("sample_store_unavailable");
                    if (!live.Retention.Enabled || !TypedDecisionSampleStore.Retains(live, PreflightTextAdapter.DecisionPoint))
                        return SampleError("retention_disabled");

                    ArmadaEvent? evt;
                    try
                    {
                        evt = await database.Events.ReadAsync(request.EventId, default).ConfigureAwait(false);
                    }
                    catch
                    {
                        logging?.Warn("[McpTypedDecisionDataTools] event metadata read failed for retained-sample lookup.");
                        return SampleError("event_unavailable");
                    }
                    if (evt == null) return SampleError("event_not_found");
                    if (!IsTypedDecisionEvent(evt.EventType)) return SampleError("not_typed_decision");

                    if (!TryReadEventPayload(evt.Payload, out TypedDecisionEventPayload? eventPayload))
                        return SampleError("event_metadata_unavailable");
                    if (!String.Equals(eventPayload!.Decision, PreflightTextAdapter.DecisionPoint, StringComparison.Ordinal))
                        return SampleError("wrong_decision_point");
                    if (String.IsNullOrWhiteSpace(eventPayload.StateSha256) || eventPayload.StateBytes < 0)
                        return SampleError("event_metadata_unavailable");

                    TypedDecisionSampleLookupResult lookup = samples.FindDecision(
                        PreflightTextAdapter.DecisionPoint,
                        evt.Id,
                        evt.CreatedUtc,
                        eventPayload.StateSha256!,
                        eventPayload.StateBytes,
                        eventPayload.ObjectiveId,
                        eventPayload.MissionId);
                    if (lookup.Availability == "not_found") return SampleError("sample_not_retained");
                    if (lookup.Availability != "available" || lookup.Sample == null)
                        return SampleError(lookup.Availability);

                    string state = lookup.Sample.RedactedState;
                    int pageLength = Math.Min(request.MaxChars, 4096);
                    if (request.Offset > state.Length || IsSurrogateBoundary(state, request.Offset))
                        return SampleError("invalid_offset");
                    int length = Math.Min(pageLength, state.Length - request.Offset);
                    if (length > 0 && request.Offset + length < state.Length
                        && Char.IsHighSurrogate(state[request.Offset + length - 1]))
                        length--;
                    if (length <= 0 && request.Offset < state.Length)
                        return SampleError("invalid_page_size");

                    string pageText = state.Substring(request.Offset, length);
                    int nextOffset = request.Offset + length;
                    return new SamplePageResponse
                    {
                        Success = true,
                        Availability = "available",
                        EventId = evt.Id,
                        DecisionPoint = PreflightTextAdapter.DecisionPoint,
                        EventCreatedUtc = evt.CreatedUtc,
                        StateSha256 = lookup.Sample.StateSha256,
                        StateBytes = eventPayload.StateBytes,
                        RedactorVersion = lookup.Sample.RedactorVersion,
                        Offset = request.Offset,
                        TotalChars = state.Length,
                        TotalUtf8Bytes = eventPayload.StateBytes,
                        PageUtf8Bytes = System.Text.Encoding.UTF8.GetByteCount(pageText),
                        PageText = pageText,
                        NextOffset = nextOffset,
                        Complete = nextOffset >= state.Length,
                        Q4SpecificPremise = null,
                        Q4RationaleStatus = "not_recorded"
                    };
                });
        }

        private static object Error(string message)
        {
            return new { Success = false, Message = message };
        }

        private static object SampleError(string availability)
        {
            return new SamplePageResponse { Success = false, Availability = availability };
        }

        private static bool IsTypedDecisionEvent(string? eventType)
        {
            return String.Equals(eventType, TypedDecisionRecorder.EventTypeGated, StringComparison.Ordinal)
                || String.Equals(eventType, TypedDecisionRecorder.EventTypeShadow, StringComparison.Ordinal)
                || String.Equals(eventType, TypedDecisionRecorder.EventTypeUnavailable, StringComparison.Ordinal)
                || String.Equals(eventType, TypedDecisionRecorder.EventTypeCaptain, StringComparison.Ordinal);
        }

        private static bool TryReadEventPayload(string? payload, out TypedDecisionEventPayload? result)
        {
            result = null;
            if (String.IsNullOrWhiteSpace(payload)) return false;
            try
            {
                result = JsonSerializer.Deserialize<TypedDecisionEventPayload>(payload);
                return result != null;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool IsSurrogateBoundary(string text, int offset)
        {
            return offset > 0 && offset < text.Length
                && Char.IsHighSurrogate(text[offset - 1]) && Char.IsLowSurrogate(text[offset]);
        }

        private sealed class ReversalRequest
        {
            public string? EventId { get; set; }
            public string? CorrectedVerdict { get; set; }
            public string? Reason { get; set; }
            public string? ReversedBy { get; set; }
        }

        private sealed class SampleRequest
        {
            public string? EventId { get; set; }
            public int Offset { get; set; }
            public int MaxChars { get; set; } = 1024;
        }

        private sealed class TypedDecisionEventPayload
        {
            [JsonRequired]
            [JsonPropertyName("decision")]
            public string? Decision { get; set; }

            [JsonRequired]
            [JsonPropertyName("state_sha256")]
            public string? StateSha256 { get; set; }

            [JsonRequired]
            [JsonPropertyName("state_bytes")]
            public int StateBytes { get; set; }

            [JsonRequired]
            [JsonPropertyName("objective_id")]
            public string? ObjectiveId { get; set; }

            [JsonRequired]
            [JsonPropertyName("mission_id")]
            public string? MissionId { get; set; }
        }

        private sealed class SamplePageResponse
        {
            /// <summary>Whether the lookup returned a usable page.</summary>
            public bool Success { get; set; }
            /// <summary>Lookup availability or refusal status.</summary>
            public string? Availability { get; set; }
            /// <summary>The typed-decision event identifier.</summary>
            public string? EventId { get; set; }
            /// <summary>The retained decision point.</summary>
            public string? DecisionPoint { get; set; }
            /// <summary>The source event creation time in UTC.</summary>
            public DateTime? EventCreatedUtc { get; set; }
            /// <summary>SHA-256 digest of the complete redacted state.</summary>
            public string? StateSha256 { get; set; }
            /// <summary>UTF-8 byte count of the complete redacted state.</summary>
            public int? StateBytes { get; set; }
            /// <summary>Redactor version that produced the retained state.</summary>
            public int? RedactorVersion { get; set; }
            /// <summary>Character offset of this page.</summary>
            public int? Offset { get; set; }
            /// <summary>Total character count of the redacted state.</summary>
            public int? TotalChars { get; set; }
            /// <summary>Total UTF-8 byte count of the redacted state.</summary>
            public int? TotalUtf8Bytes { get; set; }
            /// <summary>UTF-8 byte count of this page.</summary>
            public int? PageUtf8Bytes { get; set; }
            /// <summary>This page's redacted state text.</summary>
            public string? PageText { get; set; }
            /// <summary>Character offset for the next page.</summary>
            public int? NextOffset { get; set; }
            /// <summary>Whether this page reaches the end of the state.</summary>
            public bool? Complete { get; set; }
            /// <summary>Specific Q4 premise reference, absent when not recorded.</summary>
            public string? Q4SpecificPremise { get; set; }
            /// <summary>States whether a specific Q4 rationale was retained.</summary>
            public string? Q4RationaleStatus { get; set; }
        }
    }
}
