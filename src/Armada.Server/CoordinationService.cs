namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Shared coordination board service. Operator sessions, captains, and the admiral
    /// post notes into rooms so concurrent sessions stay aware of fleet activity.
    /// </summary>
    public class CoordinationService
    {
        #region Private-Members

        private readonly LoggingModule _Logging;
        private readonly DatabaseDriver _Database;
        private readonly WebSocket.ArmadaWebSocketHub? _WebSocketHub;
        private string _Header = "[CoordinationService] ";

        /// <summary>
        /// The key of the default fleet-wide room, created on first use.
        /// </summary>
        public static readonly string DefaultRoomKey = "fleet";

        /// <summary>
        /// Display name of the default fleet-wide room.
        /// </summary>
        public static readonly string DefaultRoomName = "Fleet";

        /// <summary>
        /// Default description of the fleet-wide room.
        /// </summary>
        public static readonly string DefaultRoomDescription =
            "Shared coordination board for every operator session and captain. Read before you act; write when you act.";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="webSocketHub">Optional WebSocket hub for live message broadcast.</param>
        public CoordinationService(LoggingModule logging, DatabaseDriver database, WebSocket.ArmadaWebSocketHub? webSocketHub = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _WebSocketHub = webSocketHub;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Ensure a room exists for the given key, creating it when missing. The default
        /// fleet room is created automatically.
        /// </summary>
        /// <param name="key">Room key.</param>
        /// <param name="name">Display name used when creating.</param>
        /// <param name="description">Description used when creating.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The existing or newly created room.</returns>
        public async Task<CoordinationRoom> EnsureRoomAsync(string key, string? name = null, string? description = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(key)) throw new ArgumentException("Room key must not be empty.", nameof(key));

            CoordinationRoom? room = await _Database.CoordinationRooms.ReadByKeyAsync(key, token).ConfigureAwait(false);
            if (room != null) return room;

            room = new CoordinationRoom
            {
                Key = key,
                Name = String.IsNullOrWhiteSpace(name) ? key : name!,
                Description = description
            };
            room = await _Database.CoordinationRooms.CreateAsync(room, token).ConfigureAwait(false);
            _Logging.Debug(_Header + "created coordination room " + key);
            return await _Database.CoordinationRooms.ReadByKeyAsync(key, token).ConfigureAwait(false)
                ?? room;
        }

        /// <summary>
        /// Post a message to a room by key.
        /// </summary>
        /// <param name="roomKey">Room key.</param>
        /// <param name="authorType">Author kind.</param>
        /// <param name="authorId">Author identifier.</param>
        /// <param name="authorName">Author display name.</param>
        /// <param name="content">Message content. Must not be empty.</param>
        /// <param name="voyageId">Optional related voyage identifier.</param>
        /// <param name="missionId">Optional related mission identifier.</param>
        /// <param name="vesselId">Optional related vessel identifier.</param>
        /// <param name="incidentId">Optional related incident identifier.</param>
        /// <param name="tenantId">Optional tenant identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created message.</returns>
        public async Task<CoordinationMessage> PostMessageAsync(
            string roomKey,
            CoordinationAuthorTypeEnum authorType,
            string? authorId,
            string authorName,
            string content,
            string? voyageId = null,
            string? missionId = null,
            string? vesselId = null,
            string? incidentId = null,
            string? tenantId = null,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(roomKey)) throw new ArgumentException("Room key must not be empty.", nameof(roomKey));
            if (String.IsNullOrEmpty(content)) throw new ArgumentException("Message content must not be empty.", nameof(content));
            if (String.IsNullOrWhiteSpace(authorName)) throw new ArgumentException("Author name must not be empty.", nameof(authorName));

            CoordinationRoom room = await EnsureRoomAsync(roomKey, DefaultRoomKey == roomKey ? DefaultRoomName : null, DefaultRoomKey == roomKey ? DefaultRoomDescription : null, token).ConfigureAwait(false);

            CoordinationMessage message = new CoordinationMessage
            {
                CoordinationRoomId = room.Id,
                TenantId = tenantId,
                AuthorType = authorType,
                AuthorId = authorId,
                AuthorName = authorName,
                Content = content,
                VoyageId = voyageId,
                MissionId = missionId,
                VesselId = vesselId,
                IncidentId = incidentId
            };
            message = await _Database.CoordinationMessages.CreateAsync(message, token).ConfigureAwait(false);

            await _Database.CoordinationRooms.UpdateAsync(room, token).ConfigureAwait(false);

            if (!String.IsNullOrEmpty(authorId))
            {
                await HeartbeatAsync(roomKey, authorId!, authorName, tenantId, token).ConfigureAwait(false);
            }

            BroadcastMessageCreated(room.Key, message);
            return message;
        }

        /// <summary>
        /// Read messages from a room in chronological order. When afterUtc is supplied,
        /// only messages created after that instant are returned.
        /// </summary>
        /// <param name="roomKey">Room key.</param>
        /// <param name="afterUtc">Optional exclusive lower bound on creation time.</param>
        /// <param name="limit">Maximum number of messages.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Messages oldest first.</returns>
        public async Task<List<CoordinationMessage>> ReadMessagesAsync(string roomKey, DateTime? afterUtc = null, int limit = 200, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(roomKey)) throw new ArgumentException("Room key must not be empty.", nameof(roomKey));

            CoordinationRoom? room = await EnsureRoomAsync(roomKey, DefaultRoomKey == roomKey ? DefaultRoomName : null, DefaultRoomKey == roomKey ? DefaultRoomDescription : null, token).ConfigureAwait(false);
            return await _Database.CoordinationMessages.EnumerateByRoomAsync(room.Id, afterUtc, limit, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Refresh a participant's presence in a room and prune stale participants.
        /// </summary>
        /// <param name="roomKey">Room key.</param>
        /// <param name="participantKey">Stable participant key.</param>
        /// <param name="displayName">Display name.</param>
        /// <param name="tenantId">Optional tenant identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The upserted participant.</returns>
        public async Task<CoordinationParticipant> HeartbeatAsync(string roomKey, string participantKey, string displayName, string? tenantId = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(roomKey)) throw new ArgumentException("Room key must not be empty.", nameof(roomKey));
            if (String.IsNullOrWhiteSpace(participantKey)) throw new ArgumentException("Participant key must not be empty.", nameof(participantKey));
            if (String.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Display name must not be empty.", nameof(displayName));

            CoordinationRoom room = await EnsureRoomAsync(roomKey, DefaultRoomKey == roomKey ? DefaultRoomName : null, DefaultRoomKey == roomKey ? DefaultRoomDescription : null, token).ConfigureAwait(false);

            CoordinationParticipant participant = new CoordinationParticipant
            {
                CoordinationRoomId = room.Id,
                TenantId = tenantId,
                ParticipantKey = participantKey,
                DisplayName = displayName
            };
            participant = await _Database.CoordinationParticipants.UpsertAsync(participant, token).ConfigureAwait(false);

            DateTime staleCutoff = DateTime.UtcNow.AddMinutes(-60);
            await _Database.CoordinationParticipants.PruneAsync(room.Id, staleCutoff, token).ConfigureAwait(false);
            return participant;
        }

        /// <summary>
        /// Enumerate participants active in a room within the given window.
        /// </summary>
        /// <param name="roomKey">Room key.</param>
        /// <param name="activeWithinMinutes">Activity window in minutes.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Participants most recently active first.</returns>
        public async Task<List<CoordinationParticipant>> EnumerateParticipantsAsync(string roomKey, int activeWithinMinutes = 15, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(roomKey)) throw new ArgumentException("Room key must not be empty.", nameof(roomKey));

            CoordinationRoom? room = await EnsureRoomAsync(roomKey, DefaultRoomKey == roomKey ? DefaultRoomName : null, DefaultRoomKey == roomKey ? DefaultRoomDescription : null, token).ConfigureAwait(false);
            return await _Database.CoordinationParticipants.EnumerateByRoomAsync(room.Id, activeWithinMinutes, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate all rooms ordered by most recent activity.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Rooms.</returns>
        public async Task<List<CoordinationRoom>> EnumerateRoomsAsync(CancellationToken token = default)
        {
            List<CoordinationRoom> rooms = await _Database.CoordinationRooms.EnumerateAsync(token).ConfigureAwait(false);
            if (rooms.Count == 0)
            {
                await EnsureRoomAsync(DefaultRoomKey, DefaultRoomName, DefaultRoomDescription, token).ConfigureAwait(false);
                rooms = await _Database.CoordinationRooms.EnumerateAsync(token).ConfigureAwait(false);
            }

            return rooms;
        }

        /// <summary>
        /// Build the system note content that mirrors a fleet event into the coordination
        /// board, or null when the event type is not mirrored.
        /// </summary>
        /// <param name="eventType">Fleet event type, for example "voyage.dispatched".</param>
        /// <param name="message">Human-readable event message.</param>
        /// <param name="entityType">Entity type carried by the event.</param>
        /// <param name="entityId">Entity identifier carried by the event.</param>
        /// <param name="voyageId">Voyage identifier, when present.</param>
        /// <param name="missionId">Mission identifier, when present.</param>
        /// <param name="vesselId">Vessel identifier, when present.</param>
        /// <returns>Note content, or null when the event type should not appear on the board.</returns>
        public static string? BuildSystemNoteContent(
            string eventType,
            string message,
            string? entityType = null,
            string? entityId = null,
            string? voyageId = null,
            string? missionId = null,
            string? vesselId = null)
        {
            if (String.IsNullOrEmpty(eventType)) return null;

            bool mirrored =
                String.Equals(eventType, "voyage.dispatched", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(eventType, "voyage.cancelled", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(eventType, "mission.completed", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(eventType, "mission.failed", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(eventType, "mission.cancelled", StringComparison.OrdinalIgnoreCase);

            if (!mirrored) return null;

            string content = "[fleet] " + message;
            if (!String.IsNullOrEmpty(voyageId)) content += " (voyage " + voyageId + ")";
            else if (!String.IsNullOrEmpty(missionId)) content += " (mission " + missionId + ")";
            else if (!String.IsNullOrEmpty(entityType) && !String.IsNullOrEmpty(entityId)) content += " (" + entityType + " " + entityId + ")";

            return content;
        }

        #endregion

        #region Private-Methods

        private void BroadcastMessageCreated(string roomKey, CoordinationMessage message)
        {
            if (_WebSocketHub == null) return;

            try
            {
                _WebSocketHub.BroadcastEvent(
                    "coordination.message.created",
                    "Coordination message created",
                    new
                    {
                        roomKey = roomKey,
                        message = message
                    });
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to broadcast coordination message: " + ex.Message);
            }
        }

        #endregion
    }
}
