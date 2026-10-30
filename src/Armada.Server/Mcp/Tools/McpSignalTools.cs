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
    using Armada.Core.Services;

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
        public static void Register(RegisterToolDelegate register, DatabaseDriver database)
        {
            register(
                "armada_send_signal",
                "Send a signal/message to a captain, or to a mission. Mail that names a mission reaches the captain that holds the mission now and follows the mission when it moves to another captain, so address a mission rather than a captain when the work matters more than who does it. The stored signal is returned. When the captain is running on a runtime that does not deliver mail mid-mission, the response includes Delivery=stored_until_next_launch and a reason that names the runtime.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        captainId = new { type = "string", description = "Target captain ID. Optional when missionId is given." },
                        missionId = new { type = "string", description = "Target mission ID (msn_ prefix). The mail goes to the mission's current captain." },
                        message = new { type = "string", description = "Signal message" }
                    },
                    required = new[] { "message" }
                },
                async (args) =>
                {
                    SignalSendArgs request = JsonSerializer.Deserialize<SignalSendArgs>(args!.Value, _JsonOptions)!;
                    string captainId = request.CaptainId;
                    string message = request.Message;

                    // A recipient is required. Without this guard an absent target deserializes to its
                    // default and a signal addressed to no captain is stored while returning success.
                    if (String.IsNullOrWhiteSpace(captainId) && String.IsNullOrWhiteSpace(request.MissionId))
                        return (object)new
                        {
                            error = "armada_send_signal requires the captainId or missionId argument.",
                            code = "missing_captain",
                            reason = "The captainId and missionId arguments were both absent or empty.",
                            action = "Pass captainId naming the captain the signal is for, or missionId naming the mission."
                        };

                    // message is required. Without this guard an absent message deserializes to its
                    // default empty string and an EMPTY Mail is stored while returning success, so a
                    // caller cannot tell the signal carried nothing.
                    if (String.IsNullOrWhiteSpace(message))
                        return (object)new
                        {
                            error = "armada_send_signal requires the message argument.",
                            code = "missing_message",
                            reason = "The message argument was absent or empty.",
                            action = "Pass message carrying the signal text to send to the captain."
                        };

                    // The signal is owned by the authenticated caller, exactly as a REST create is.
                    AuthContext sendCaller = McpCallerContext.Require();
                    Signal signal = new Signal(SignalTypeEnum.Mail, message);
                    signal.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(sendCaller);
                    signal.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(sendCaller);
                    signal.ToCaptainId = String.IsNullOrWhiteSpace(captainId) ? null : captainId;
                    if (!String.IsNullOrWhiteSpace(request.MissionId))
                    {
                        Mission? addressed = await Armada.Core.Authorization.CallerScopedRead.ReadMissionAsync(database, sendCaller, request.MissionId).ConfigureAwait(false);
                        if (addressed != null)
                            MissionMailRouting.AddressToMission(signal, addressed);
                        else
                            return (object)new
                            {
                                error = "Mission not found: " + request.MissionId,
                                code = "mission_not_found",
                                reason = "No mission has the missionId argument as its identifier.",
                                action = "Pass the identifier of an existing mission."
                            };
                    }
                    if (!await SignalDelivery.CanAddressCaptainAsync(database, sendCaller, signal.ToCaptainId).ConfigureAwait(false))
                        return (object)new
                        {
                            error = "Captain not found: " + signal.ToCaptainId,
                            code = "captain_not_found",
                            reason = "No captain in the caller's tenant has the captainId argument as its identifier.",
                            action = "Pass the identifier of a captain in your tenant, or missionId naming the mission."
                        };
                    signal = await database.Signals.CreateAsync(signal).ConfigureAwait(false);
                    signal = await SignalDelivery.AnnotateAsync(database, signal).ConfigureAwait(false);
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
                    CollectionLimits.RequireRequestItems(request.Ids, "Ids");

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
                        CreatedBy = McpCallerContext.CallerIdentity ?? request.CreatedBy
                    };

                    AuthContext nudgeCaller = McpCallerContext.Require();
                    Signal signal = new Signal(signalType, JsonSerializer.Serialize(payload, _JsonOptions));
                    signal.TenantId = Armada.Core.Authorization.OwnershipPolicy.TenantOf(nudgeCaller);
                    signal.UserId = Armada.Core.Authorization.OwnershipPolicy.UserOf(nudgeCaller);
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

                    if (signal.Read)
                        return (object)new { Status = "already_read", SignalId = request.SignalId };

                    await database.Signals.MarkReadAsync(request.SignalId).ConfigureAwait(false);
                    return (object)new { Status = "marked", SignalId = request.SignalId };
                });
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
