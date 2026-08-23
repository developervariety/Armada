namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using ArmadaConstants = Armada.Core.Constants;

    /// <summary>
    /// Registers MCP tools for the shared coordination board (chatroom) that keeps
    /// concurrent operator sessions aware of each other's work.
    /// </summary>
    public static class McpCoordinationTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Registers coordination MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for coordination data access.</param>
        /// <param name="coordination">Coordination service.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, CoordinationService coordination)
        {
            register(
                "armada_coordination_post",
                "Post a note to the shared coordination board. Use this so other operator sessions know what you are doing: claim work before you start, and report outcomes when you finish. Other sessions read this board before acting.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        content = new { type = "string", description = "Note content. Must not be empty." },
                        authorName = new { type = "string", description = "Your session or operator display name, for example 'claude-session-1'." },
                        authorId = new { type = "string", description = "Optional stable identifier for your session. Defaults to authorName when omitted." },
                        roomKey = new { type = "string", description = "Room key. Omit for the default fleet room." },
                        voyageId = new { type = "string", description = "Optional related voyage ID (vyg_ prefix)." },
                        missionId = new { type = "string", description = "Optional related mission ID (msn_ prefix)." },
                        vesselId = new { type = "string", description = "Optional related vessel ID (vsl_ prefix)." },
                        incidentId = new { type = "string", description = "Optional related incident ID (inc_ prefix)." }
                    },
                    required = new[] { "content", "authorName" }
                },
                async (args) =>
                {
                    CoordinationPostArgs request = JsonSerializer.Deserialize<CoordinationPostArgs>(args!.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.Content))
                        return (object)new { Error = "content is required and must not be empty" };
                    if (String.IsNullOrWhiteSpace(request.AuthorName))
                        return (object)new { Error = "authorName is required and must not be empty" };

                    string roomKey = String.IsNullOrWhiteSpace(request.RoomKey) ? CoordinationService.DefaultRoomKey : request.RoomKey!;
                    string authorId = String.IsNullOrWhiteSpace(request.AuthorId) ? request.AuthorName! : request.AuthorId!;

                    try
                    {
                        CoordinationMessage message = await coordination.PostMessageAsync(
                            roomKey,
                            CoordinationAuthorTypeEnum.Operator,
                            authorId,
                            request.AuthorName!,
                            request.Content!,
                            request.VoyageId,
                            request.MissionId,
                            request.VesselId,
                            request.IncidentId,
                            ArmadaConstants.DefaultTenantId).ConfigureAwait(false);
                        return (object)message;
                    }
                    catch (NotSupportedException ex)
                    {
                        return (object)new { Error = ex.Message };
                    }
                });

            register(
                "armada_coordination_read",
                "Read recent notes from the shared coordination board plus who is currently active. Read this before dispatching voyages or touching incidents so you do not duplicate another session's work.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        roomKey = new { type = "string", description = "Room key. Omit for the default fleet room." },
                        limit = new { type = "integer", description = "Maximum number of notes to return (default 50)." },
                        afterUtc = new { type = "string", description = "Optional ISO 8601 timestamp; returns only notes created after it." },
                        activeWithinMinutes = new { type = "integer", description = "Presence window in minutes (default 15)." }
                    }
                },
                async (args) =>
                {
                    CoordinationReadArgs request = args != null && args.HasValue
                        ? JsonSerializer.Deserialize<CoordinationReadArgs>(args.Value, _JsonOptions) ?? new CoordinationReadArgs()
                        : new CoordinationReadArgs();

                    string roomKey = String.IsNullOrWhiteSpace(request.RoomKey) ? CoordinationService.DefaultRoomKey : request.RoomKey!;
                    int limit = request.Limit.HasValue && request.Limit.Value > 0 ? request.Limit.Value : 50;
                    int activeWithinMinutes = request.ActiveWithinMinutes.HasValue && request.ActiveWithinMinutes.Value > 0 ? request.ActiveWithinMinutes.Value : 15;

                    try
                    {
                        List<CoordinationMessage> messages = await coordination.ReadMessagesAsync(roomKey, request.AfterUtc, limit).ConfigureAwait(false);
                        List<CoordinationParticipant> participants = await coordination.EnumerateParticipantsAsync(roomKey, activeWithinMinutes).ConfigureAwait(false);
                        return (object)new { RoomKey = roomKey, Messages = messages, ActiveParticipants = participants };
                    }
                    catch (NotSupportedException ex)
                    {
                        return (object)new { Error = ex.Message };
                    }
                });

            register(
                "armada_coordination_heartbeat",
                "Send a presence heartbeat to the shared coordination board. Call periodically while working so other sessions can see you are active, and to prune stale presence.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        participantKey = new { type = "string", description = "Stable identifier for your session." },
                        displayName = new { type = "string", description = "Display name shown to other sessions." },
                        roomKey = new { type = "string", description = "Room key. Omit for the default fleet room." }
                    },
                    required = new[] { "participantKey", "displayName" }
                },
                async (args) =>
                {
                    CoordinationHeartbeatArgs request = JsonSerializer.Deserialize<CoordinationHeartbeatArgs>(args!.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.ParticipantKey))
                        return (object)new { Error = "participantKey is required" };
                    if (String.IsNullOrWhiteSpace(request.DisplayName))
                        return (object)new { Error = "displayName is required" };

                    string roomKey = String.IsNullOrWhiteSpace(request.RoomKey) ? CoordinationService.DefaultRoomKey : request.RoomKey!;

                    try
                    {
                        CoordinationParticipant participant = await coordination.HeartbeatAsync(
                            roomKey,
                            request.ParticipantKey!,
                            request.DisplayName!,
                            ArmadaConstants.DefaultTenantId).ConfigureAwait(false);
                        return (object)participant;
                    }
                    catch (NotSupportedException ex)
                    {
                        return (object)new { Error = ex.Message };
                    }
                });
        }

        private sealed class CoordinationPostArgs
        {
            public string Content { get; set; } = "";
            public string AuthorName { get; set; } = "";
            public string? AuthorId { get; set; } = null;
            public string? RoomKey { get; set; } = null;
            public string? VoyageId { get; set; } = null;
            public string? MissionId { get; set; } = null;
            public string? VesselId { get; set; } = null;
            public string? IncidentId { get; set; } = null;
        }

        private sealed class CoordinationReadArgs
        {
            public string? RoomKey { get; set; } = null;
            public int? Limit { get; set; } = null;
            public DateTime? AfterUtc { get; set; } = null;
            public int? ActiveWithinMinutes { get; set; } = null;
        }

        private sealed class CoordinationHeartbeatArgs
        {
            public string ParticipantKey { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string? RoomKey { get; set; } = null;
        }
    }
}
