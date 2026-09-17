namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Services;
    using Armada.Core.Services.TypedDecisions;
    using Armada.Core.Settings;
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

        /// <summary>
        /// Register both tools.
        /// </summary>
        /// <param name="register">Tool registration delegate.</param>
        /// <param name="recorder">The typed-decision recorder that writes the reversal event.</param>
        /// <param name="samples">The host-local sample store, or null when retention is not configured.</param>
        /// <param name="settings">Live typed-decision settings, read per call.</param>
        /// <param name="logging">Optional logging module.</param>
        public static void Register(
            RegisterToolDelegate register,
            TypedDecisionRecorder? recorder,
            TypedDecisionSampleStore? samples,
            Func<TypedDecisionSettings?> settings,
            LoggingModule? logging)
        {
            if (register == null) throw new ArgumentNullException(nameof(register));
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
                        request = JsonSerializer.Deserialize<ReversalRequest>(args.Value.GetRawText(), _JsonOptions);
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
                    List<string> optedIn = new List<string>();
                    foreach (KeyValuePair<string, TypedDecisionRuleSettings> entry in live.Decisions)
                    {
                        if (entry.Value != null && entry.Value.RetainState) optedIn.Add(entry.Key);
                    }
                    optedIn.Sort(StringComparer.Ordinal);

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
        }

        private static object Error(string message)
        {
            return new { Success = false, Message = message };
        }

        private sealed class ReversalRequest
        {
            public string? EventId { get; set; }
            public string? CorrectedVerdict { get; set; }
            public string? Reason { get; set; }
            public string? ReversedBy { get; set; }
        }
    }
}
