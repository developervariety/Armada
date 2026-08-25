namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core;
    using ArmadaConstants = Armada.Core.Constants;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Registers MCP tools for signal operations (send, delete).
    /// </summary>
    public static class McpSignalTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Registers signal MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for signal data access.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, Func<string?>? effectiveWakeOwnerKey = null)
        {
            register(
                "armada_send_signal",
                "Send a signal/message to a captain",
                new
                {
                    type = "object",
                    properties = new
                    {
                        captainId = new { type = "string", description = "Target captain ID" },
                        message = new { type = "string", description = "Signal message" }
                    },
                    required = new[] { "captainId", "message" }
                },
                async (args) =>
                {
                    SignalSendArgs request = JsonSerializer.Deserialize<SignalSendArgs>(args!.Value, _JsonOptions)!;
                    string captainId = request.CaptainId;
                    string message = request.Message;
                    Signal signal = new Signal(SignalTypeEnum.Mail, message);
                    signal.TenantId = ArmadaConstants.DefaultTenantId;
                    signal.ToCaptainId = captainId;
                    signal = await database.Signals.CreateAsync(signal).ConfigureAwait(false);
                    return (object)signal;
                });

            register(
                "armada_delete_signals",
                "Soft-delete multiple signals by marking them as read. Returns a summary of deleted and skipped entries.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        ids = new { type = "array", items = new { type = "string" }, description = "List of signal IDs to delete (sig_ prefix)" }
                    },
                    required = new[] { "ids" }
                },
                async (args) =>
                {
                    DeleteMultipleArgs request = JsonSerializer.Deserialize<DeleteMultipleArgs>(args!.Value, _JsonOptions)!;
                    if (request.Ids == null || request.Ids.Count == 0)
                        return (object)new { Error = "ids is required and must not be empty" };

                    DeleteMultipleResult result = new DeleteMultipleResult();
                    foreach (string id in request.Ids)
                    {
                        if (String.IsNullOrEmpty(id))
                        {
                            result.Skipped.Add(new DeleteMultipleSkipped(id ?? "", "Empty ID"));
                            continue;
                        }
                        Signal? signal = await database.Signals.ReadAsync(id).ConfigureAwait(false);
                        if (signal == null)
                        {
                            result.Skipped.Add(new DeleteMultipleSkipped(id, "Not found"));
                            continue;
                        }
                        await database.Signals.MarkReadAsync(id).ConfigureAwait(false);
                        result.Deleted++;
                    }
                    result.ResolveStatus();
                    return (object)result;
                });

            register(
                "armada_nudge_voyage",
                "Send a Nudge or Mail signal to a voyage or mission mailbox. The signal is injected into downstream mission briefs at the pipeline handoff boundary. Exactly one of voyageId or missionId must be supplied.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        voyageId = new { type = "string", description = "Target voyage ID (vyg_ prefix). Exactly one of voyageId or missionId is required." },
                        missionId = new { type = "string", description = "Target mission ID (msn_ prefix). Exactly one of voyageId or missionId is required." },
                        type = new { type = "string", description = "Signal type: Nudge or Mail." },
                        message = new { type = "string", description = "Note content. Must not be empty." },
                        createdBy = new { type = "string", description = "Optional creator identifier (captain id, user id, or system)." }
                    },
                    required = new[] { "type", "message" }
                },
                async (args) =>
                {
                    NudgeVoyageArgs request = JsonSerializer.Deserialize<NudgeVoyageArgs>(args!.Value, _JsonOptions)!;

                    bool hasVoyage = !String.IsNullOrEmpty(request.VoyageId);
                    bool hasMission = !String.IsNullOrEmpty(request.MissionId);

                    if (!hasVoyage && !hasMission)
                        return (object)new { Error = "Exactly one of voyageId or missionId is required" };
                    if (hasVoyage && hasMission)
                        return (object)new { Error = "Exactly one of voyageId or missionId is required; both were provided" };
                    if (String.IsNullOrEmpty(request.Message))
                        return (object)new { Error = "message is required and must not be empty" };
                    if (!String.Equals(request.Type, "Nudge", StringComparison.OrdinalIgnoreCase) &&
                        !String.Equals(request.Type, "Mail", StringComparison.OrdinalIgnoreCase))
                        return (object)new { Error = "type must be Nudge or Mail" };

                    SignalTypeEnum signalType = String.Equals(request.Type, "Nudge", StringComparison.OrdinalIgnoreCase)
                        ? SignalTypeEnum.Nudge
                        : SignalTypeEnum.Mail;

                    string? resolvedVoyageId = request.VoyageId;
                    string? resolvedMissionId = request.MissionId;

                    if (hasMission)
                    {
                        Mission? mission = await database.Missions.ReadAsync(resolvedMissionId!).ConfigureAwait(false);
                        if (mission == null)
                            return (object)new { Error = "Mission not found: " + resolvedMissionId };
                        if (!String.IsNullOrEmpty(mission.VoyageId))
                            resolvedVoyageId = mission.VoyageId;
                    }
                    else
                    {
                        Voyage? voyage = await database.Voyages.ReadAsync(resolvedVoyageId!).ConfigureAwait(false);
                        if (voyage == null)
                            return (object)new { Error = "Voyage not found: " + resolvedVoyageId };
                    }

                    VoyageMailboxSignalPayload payload = new VoyageMailboxSignalPayload
                    {
                        MissionId = resolvedMissionId,
                        VoyageId = resolvedVoyageId,
                        Message = request.Message,
                        CreatedBy = ArmadaMcpHttpServer.CurrentParticipantKey ?? request.CreatedBy
                    };

                    Signal signal = new Signal(signalType, JsonSerializer.Serialize(payload, _JsonOptions));
                    signal.TenantId = ArmadaConstants.DefaultTenantId;
                    signal = await database.Signals.CreateAsync(signal).ConfigureAwait(false);
                    return (object)signal;
                });

            register(
                "armada_mark_signal_read",
                "Mark a single signal as read (idempotent). Used by interactive orchestrators draining AgentWake notifications via armada_enumerate entityType=signals signalType=Wake unreadOnly=true. Returns status=marked when the signal flipped from unread to read, already_read when the signal was already marked, or not_found when no signal exists with the given id.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        signalId = new { type = "string", description = "Signal ID (sig_ prefix) to acknowledge." }
                    },
                    required = new[] { "signalId" }
                },
                async (args) =>
                {
                    MarkSignalReadArgs request = JsonSerializer.Deserialize<MarkSignalReadArgs>(args!.Value, _JsonOptions)!;
                    if (String.IsNullOrEmpty(request.SignalId))
                        return (object)new { Error = "signalId is required" };

                    Signal? signal = await database.Signals.ReadAsync(request.SignalId).ConfigureAwait(false);
                    if (signal == null)
                        return (object)new { Status = "not_found", SignalId = request.SignalId };

                    string? participantKey = ArmadaMcpHttpServer.CurrentParticipantKey;
                    string? refusal = WakeAcknowledgementRefusal(participantKey, signal, effectiveWakeOwnerKey?.Invoke());
                    if (refusal != null)
                        return (object)new { Error = refusal };

                    if (signal.Read)
                        return (object)new { Status = "already_read", SignalId = request.SignalId };

                    await database.Signals.MarkReadAsync(request.SignalId).ConfigureAwait(false);
                    return (object)new { Status = "marked", SignalId = request.SignalId };
                });
        }

        /// <summary>
        /// Decides whether an authenticated participant may acknowledge a signal. Returns null when
        /// the acknowledgement is allowed, otherwise the refusal text.
        /// <para>
        /// An anonymous caller (no participant header) may acknowledge anything, unchanged. An
        /// authenticated participant may acknowledge only Wake signals: a Wake addressed with a
        /// <c>[to=&lt;key&gt;]</c> prefix belongs to that key alone, and an UNADDRESSED Wake (a
        /// mission-outcome or critical wake, prefixed <c>[vsl=...]</c> or <c>[CRITICAL]</c> or
        /// nothing) belongs to the effective AgentWake participant, because that is the session
        /// the wake starts. Without that owner rule a broadcast Wake had no legal acknowledger
        /// that sends the header, so the lead it woke could never mark it read.
        /// </para>
        /// </summary>
        /// <param name="participantKey">The caller's participant key, or null when anonymous.</param>
        /// <param name="signal">The signal being acknowledged.</param>
        /// <param name="effectiveWakeOwnerKey">The effective AgentWake participant key, or null when none is registered.</param>
        /// <returns>Null when allowed; otherwise the refusal message.</returns>
        internal static string? WakeAcknowledgementRefusal(string? participantKey, Signal signal, string? effectiveWakeOwnerKey)
        {
            if (String.IsNullOrWhiteSpace(participantKey)) return null;
            if (signal.Type != SignalTypeEnum.Wake || String.IsNullOrEmpty(signal.Payload))
                return "An authenticated participant can acknowledge only a Wake signal.";

            const string addressedPrefix = "[to=";
            if (signal.Payload.StartsWith(addressedPrefix, StringComparison.Ordinal))
            {
                string ownPrefix = addressedPrefix + participantKey + "]";
                if (signal.Payload.StartsWith(ownPrefix, StringComparison.Ordinal)) return null;
                return "An authenticated participant can acknowledge only its own Wake signal.";
            }

            if (!String.IsNullOrWhiteSpace(effectiveWakeOwnerKey)
                && String.Equals(participantKey, effectiveWakeOwnerKey, StringComparison.Ordinal))
                return null;

            return "An unaddressed Wake belongs to the effective AgentWake participant ("
                + (String.IsNullOrWhiteSpace(effectiveWakeOwnerKey) ? "none registered" : effectiveWakeOwnerKey)
                + "); only that key, or an anonymous caller, can acknowledge it.";
        }

        private sealed class MarkSignalReadArgs
        {
            public string SignalId { get; set; } = string.Empty;
        }

        private sealed class NudgeVoyageArgs
        {
            public string? VoyageId { get; set; } = null;
            public string? MissionId { get; set; } = null;
            public string Type { get; set; } = "";
            public string Message { get; set; } = "";
            public string? CreatedBy { get; set; } = null;
        }
    }
}
