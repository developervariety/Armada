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
        public static void Register(RegisterToolDelegate register, DatabaseDriver database)
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

            register(
                "armada_nudge_voyage",
                "Send a Nudge or Mail reply to a specific mission in a voyage. If the mission is WaitingForInput the reply text is prepended under [ORCHESTRATOR NOTES] at the top of the mission description, the mission is returned to Pending, stale process/dock/captain/agent-output fields are cleared, and the created reply signal is marked read. For missions in other statuses the reply is stored as an unread Mail signal.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        voyageId = new { type = "string", description = "Voyage ID (vyg_ prefix)" },
                        missionId = new { type = "string", description = "Mission ID (msn_ prefix) to target" },
                        message = new { type = "string", description = "Reply text to send" }
                    },
                    required = new[] { "voyageId", "missionId", "message" }
                },
                async (args) =>
                {
                    NudgeVoyageArgs request = JsonSerializer.Deserialize<NudgeVoyageArgs>(args!.Value, _JsonOptions)!;
                    if (String.IsNullOrEmpty(request.VoyageId))
                        return (object)new { Error = "voyageId is required" };
                    if (String.IsNullOrEmpty(request.MissionId))
                        return (object)new { Error = "missionId is required" };
                    if (String.IsNullOrEmpty(request.Message))
                        return (object)new { Error = "message is required" };

                    Mission? mission = await database.Missions.ReadAsync(request.MissionId).ConfigureAwait(false);
                    if (mission == null)
                        return (object)new { Error = "mission not found: " + request.MissionId };

                    if (!String.Equals(mission.VoyageId, request.VoyageId, StringComparison.Ordinal))
                        return (object)new { Error = "mission " + request.MissionId + " does not belong to voyage " + request.VoyageId };

                    Signal replySignal = new Signal(SignalTypeEnum.Mail, request.Message);
                    replySignal.TenantId = ArmadaConstants.DefaultTenantId;
                    replySignal.ToCaptainId = mission.CaptainId;
                    replySignal = await database.Signals.CreateAsync(replySignal).ConfigureAwait(false);

                    if (mission.Status == MissionStatusEnum.WaitingForInput)
                    {
                        string notes = "[ORCHESTRATOR NOTES]\n" + request.Message.Trim() + "\n\n";
                        mission.Description = notes + (mission.Description ?? "");
                        mission.Status = MissionStatusEnum.Pending;
                        mission.CaptainId = null;
                        mission.ProcessId = null;
                        mission.DockId = null;
                        mission.AgentOutput = null;
                        mission.LastUpdateUtc = DateTime.UtcNow;
                        await database.Missions.UpdateAsync(mission).ConfigureAwait(false);
                        await database.Signals.MarkReadAsync(replySignal.Id).ConfigureAwait(false);
                        return (object)new
                        {
                            Status = "resumed",
                            MissionId = mission.Id,
                            MissionStatus = mission.Status.ToString(),
                            SignalId = replySignal.Id
                        };
                    }

                    return (object)new
                    {
                        Status = "queued",
                        MissionId = mission.Id,
                        MissionStatus = mission.Status.ToString(),
                        SignalId = replySignal.Id
                    };
                });
        }

        private sealed class MarkSignalReadArgs
        {
            public string SignalId { get; set; } = string.Empty;
        }

        private sealed class NudgeVoyageArgs
        {
            public string VoyageId { get; set; } = string.Empty;
            public string MissionId { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
        }
    }
}
