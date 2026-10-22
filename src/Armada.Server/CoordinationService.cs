namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
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
        private readonly KeyedAsyncLock _CaptainNoteLocks = new KeyedAsyncLock(StringComparer.Ordinal);
        private const int _MaximumCaptainNoteLength = 500;
        private const int _MaximumCaptainNotesPerMission = 20;
        private static readonly TimeSpan _CampaignVoyageCacheLifetime = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan _NoCampaignVoyageCacheLifetime = TimeSpan.FromSeconds(60);
        private const int _MaximumCampaignVoyageCacheEntries = 512;
        private readonly object _CampaignVoyageCacheLock = new object();
        private readonly Dictionary<string, CampaignVoyageCacheEntry> _CampaignVoyageCache = new Dictionary<string, CampaignVoyageCacheEntry>(StringComparer.Ordinal);
        private readonly TimeProvider _TimeProvider;
        private static readonly TimeSpan _ParticipantNameRetention = TimeSpan.FromMinutes(60);
        private static readonly TimeSpan _SharedKeyWarningWindow = TimeSpan.FromMinutes(15);
        private const int _MaximumTrackedParticipantKeys = 1024;
        private readonly object _ParticipantNameLock = new object();
        private readonly Dictionary<string, ParticipantNameHistory> _ParticipantNames = new Dictionary<string, ParticipantNameHistory>(StringComparer.Ordinal);

        /// <summary>
        /// Event type written once per participant key per presence window when the key is in use
        /// under several names.
        /// </summary>
        public const string SharedParticipantKeyEventType = "coordination.shared_participant_key";

        /// <summary>
        /// Optional bridge to the AgentWake wake path. Invoked with the target
        /// participant key (null = the registered AgentWake session) and the wake
        /// text. When set, it owns wake delivery for addressed notes and hold
        /// events; when null, addressed notes fall back to writing the Wake
        /// signal row directly.
        /// </summary>
        public Func<string?, string, CancellationToken, Task>? BoardWakeEmitter { get; set; }

        /// <summary>
        /// The key of the default fleet-wide room, created on first use.
        /// </summary>
        public static readonly string DefaultRoomKey = CoordinationRoom.DefaultKey;

        /// <summary>
        /// Display name of the default fleet-wide room.
        /// </summary>
        public static readonly string DefaultRoomName = "Fleet";

        /// <summary>
        /// Default description of the fleet-wide room.
        /// </summary>
        public static readonly string DefaultRoomDescription =
            "Shared coordination board for every operator session and captain. Read before you act; write when you act.";

        /// <summary>
        /// Default description of a campaign room.
        /// </summary>
        public static readonly string CampaignRoomDescription =
            "Coordination room for one campaign. Claims and notes for the campaign's objectives land here; the fleet room stays the all-hands channel.";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver.</param>
        /// <param name="webSocketHub">Optional WebSocket hub for live message broadcast.</param>
        /// <param name="timeProvider">Optional clock for presence-name tracking; the system clock when null.</param>
        public CoordinationService(LoggingModule logging, DatabaseDriver database, WebSocket.ArmadaWebSocketHub? webSocketHub = null, TimeProvider? timeProvider = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _WebSocketHub = webSocketHub;
            _TimeProvider = timeProvider ?? TimeProvider.System;
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
            key = CoordinationRoom.NormalizeKey(key);
            if (key == DefaultRoomKey)
            {
                if (String.IsNullOrWhiteSpace(name)) name = DefaultRoomName;
                if (String.IsNullOrWhiteSpace(description)) description = DefaultRoomDescription;
            }
            else if (CampaignRoomResolver.IsCampaignRoomKey(key))
            {
                if (String.IsNullOrWhiteSpace(name)) name = "Campaign " + key.Substring(CampaignRoomResolver.RoomKeyPrefix.Length);
                if (String.IsNullOrWhiteSpace(description)) description = CampaignRoomDescription;
            }

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
        /// Resolve the room a claim or note lands in. An explicit room key wins. Without one, an
        /// objective that belongs to a campaign routes to that campaign's room, which is created on
        /// first use. Everything else lands in the fleet room.
        /// </summary>
        /// <param name="roomKey">Caller-supplied room key, or null.</param>
        /// <param name="objectiveId">Objective the claim or note concerns, or null.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The room key to use.</returns>
        public async Task<string> ResolveRoomKeyAsync(string? roomKey, string? objectiveId, CancellationToken token = default)
        {
            if (!String.IsNullOrWhiteSpace(roomKey)) return CoordinationRoom.NormalizeKey(roomKey);
            string? campaignKey = await CampaignRoomResolver.ResolveRoomKeyForObjectiveAsync(_Database, objectiveId, token).ConfigureAwait(false);
            return campaignKey ?? DefaultRoomKey;
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
            string? toParticipantKey = null,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(roomKey)) throw new ArgumentException("Room key must not be empty.", nameof(roomKey));
            if (String.IsNullOrEmpty(content)) throw new ArgumentException("Message content must not be empty.", nameof(content));
            if (String.IsNullOrWhiteSpace(authorName)) throw new ArgumentException("Author name must not be empty.", nameof(authorName));

            CoordinationAddressResolution address = await ResolveAddressAsync(toParticipantKey, token).ConfigureAwait(false);
            if (address.Notice != null)
                _Logging.Warn(_Header + "note from " + authorName + ": " + address.Notice);
            toParticipantKey = address.ResolvedKey;

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
                IncidentId = incidentId,
                ToParticipantKey = String.IsNullOrWhiteSpace(toParticipantKey) ? null : toParticipantKey
            };
            message = await _Database.CoordinationMessages.CreateAsync(message, token).ConfigureAwait(false);
            message.AddressNotice = address.Notice;

            await _Database.CoordinationRooms.UpdateAsync(room, token).ConfigureAwait(false);

            if (!String.IsNullOrEmpty(authorId))
            {
                await HeartbeatAsync(roomKey, authorId!, authorName, tenantId, token).ConfigureAwait(false);
            }

            BroadcastMessageCreated(room.Key, message);

            if (!String.IsNullOrWhiteSpace(toParticipantKey))
            {
                await EmitWakeForAddressedNoteAsync(toParticipantKey!, authorName, content, token).ConfigureAwait(false);
            }

            return message;
        }

        /// <summary>Store a bounded note from the captain assigned to a mission.</summary>
        /// <param name="captain">Captain that emitted the marker.</param>
        /// <param name="mission">Mapped mission.</param>
        /// <param name="content">Untrusted marker content.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored note, or null when content is invalid or the mission limit is reached.</returns>
        public async Task<CoordinationMessage?> PostCaptainNoteAsync(Captain captain, Mission mission, string content, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (String.IsNullOrWhiteSpace(mission.Id) || String.IsNullOrWhiteSpace(captain.Id)
                || !String.Equals(mission.CaptainId, captain.Id, StringComparison.Ordinal)) return null;

            string safeContent = PapercutParser.RedactSecrets(content ?? String.Empty).Trim();
            if (safeContent.Length == 0 || safeContent.Length > _MaximumCaptainNoteLength) return null;

            using (await _CaptainNoteLocks.AcquireAsync(mission.Id, token).ConfigureAwait(false))
            {
                long count = await _Database.CoordinationMessages.CountCaptainMessagesByMissionAsync(mission.Id, token).ConfigureAwait(false);
                if (count >= _MaximumCaptainNotesPerMission) return null;

                return await PostMessageAsync(
                    DefaultRoomKey,
                    CoordinationAuthorTypeEnum.Captain,
                    captain.Id,
                    captain.Name,
                    safeContent,
                    mission.VoyageId,
                    mission.Id,
                    mission.VesselId,
                    tenantId: mission.TenantId ?? captain.TenantId,
                    token: token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Writes a Wake signal for an addressed note so the target session's next poll
        /// (coordination read, heartbeat, or a signals drain) sees a targeted "you have
        /// mail" instead of having to re-read the whole room. Payload prefix
        /// "[to=&lt;key&gt;]" is the routing contract shared with the read and heartbeat
        /// tools; best-effort, never fails the post.
        /// </summary>
        private async Task EmitWakeForAddressedNoteAsync(string toParticipantKey, string authorName, string content, CancellationToken token)
        {
            string body = content ?? String.Empty;
            if (body.Length > 500) body = body.Substring(0, 500);
            string text = "[from=" + authorName + "] " + body;

            if (BoardWakeEmitter != null)
            {
                try
                {
                    await BoardWakeEmitter(toParticipantKey, text, token).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "board wake emitter failed; falling back to signal row: " + ex.Message);
                }
            }

            try
            {
                Signal wake = new Signal(SignalTypeEnum.Wake, "[to=" + toParticipantKey + "] " + text);
                wake.TenantId = Armada.Core.Constants.DefaultTenantId;
                await _Database.Signals.CreateAsync(wake, token).ConfigureAwait(false);
            }
            catch (NotSupportedException ex)
            {
                _Logging.Debug(_Header + "wake unsupported on this backend: " + ex.Message);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to emit wake for addressed note: " + ex.Message);
            }
        }

        /// <summary>
        /// Emit a wake for a fleet-wide event (dispatch hold engage/clear) targeted at
        /// the registered AgentWake session, if one is registered with a participant key.
        /// </summary>
        public async Task EmitHoldWakeAsync(string text, CancellationToken token = default)
        {
            if (BoardWakeEmitter == null) return;
            try
            {
                await BoardWakeEmitter(null, text, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "hold wake failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Enumerate unread Wake signals addressed to a participant key (payload prefix
        /// "[to=&lt;key&gt;]"), oldest first.
        /// </summary>
        public async Task<List<Signal>> EnumerateUnreadWakesAsync(string participantKey, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(participantKey)) return new List<Signal>();

            EnumerationQuery query = new EnumerationQuery
            {
                SignalType = SignalTypeEnum.Wake.ToString(),
                UnreadOnly = true,
                PageSize = 50
            };
            EnumerationResult<Signal> result = await _Database.Signals.EnumerateAsync(query, token).ConfigureAwait(false);
            string prefix = "[to=" + participantKey + "]";
            return result.Objects.Where(w => w.Payload != null && w.Payload.StartsWith(prefix, StringComparison.Ordinal)).ToList();
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
        public async Task<List<CoordinationMessage>> ReadMessagesAsync(string roomKey, DateTime? afterUtc = null, int limit = 200, CancellationToken token = default, string? visibleToParticipantKey = null)
        {
            if (String.IsNullOrWhiteSpace(roomKey)) throw new ArgumentException("Room key must not be empty.", nameof(roomKey));

            CoordinationRoom? room = await EnsureRoomAsync(roomKey, DefaultRoomKey == roomKey ? DefaultRoomName : null, DefaultRoomKey == roomKey ? DefaultRoomDescription : null, token).ConfigureAwait(false);
            if (!String.IsNullOrWhiteSpace(visibleToParticipantKey))
            {
                return await _Database.CoordinationMessages.EnumerateVisibleToAsync(
                    room.Id, visibleToParticipantKey, afterUtc, limit, token).ConfigureAwait(false);
            }

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

            CoordinationSharedParticipantKey? newlyShared = RecordParticipantName(participantKey, displayName);
            if (newlyShared != null)
                await RecordSharedParticipantKeyEventAsync(newlyShared, tenantId, token).ConfigureAwait(false);

            // A live session keeps its own reservations alive by heartbeating, in whichever room
            // each claim was made: a claim routed to a campaign room must not lapse while its holder
            // heartbeats in the fleet room.
            DateTime extendedExpiry = DateTime.UtcNow.AddHours(4);
            List<CoordinationRoom> rooms = await _Database.CoordinationRooms.EnumerateAsync(token).ConfigureAwait(false);
            if (!rooms.Any(r => String.Equals(r.Id, room.Id, StringComparison.Ordinal))) rooms.Add(room);
            foreach (CoordinationRoom claimRoom in rooms)
            {
                await _Database.CoordinationClaims.ExtendActiveForParticipantAsync(
                    claimRoom.Id, participant.ParticipantKey, extendedExpiry, token).ConfigureAwait(false);
            }

            DateTime staleCutoff = DateTime.UtcNow.AddMinutes(-60);
            await _Database.CoordinationParticipants.PruneAsync(room.Id, staleCutoff, token).ConfigureAwait(false);
            return participant;
        }

        /// <summary>
        /// Resolve the addressee of a note. A key that a participant uses (a presence row in any room
        /// or a post or heartbeat since the admiral started) is used as is. A value that is no such
        /// key but that exactly one participant key uses as its name resolves to that key, so the
        /// note and its wake reach the session that reads and acknowledges as that key. A name
        /// several keys use, or a value nobody uses, stays as given.
        /// </summary>
        /// <param name="toParticipantKey">Addressee the sender gave; null or blank for a broadcast note.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The resolution, with a sender-facing notice when the value was a name.</returns>
        public async Task<CoordinationAddressResolution> ResolveAddressAsync(string? toParticipantKey, CancellationToken token = default)
        {
            CoordinationAddressResolution resolution = new CoordinationAddressResolution();
            if (String.IsNullOrWhiteSpace(toParticipantKey)) return resolution;
            string requested = toParticipantKey.Trim();
            resolution.RequestedKey = requested;
            resolution.ResolvedKey = requested;

            Dictionary<string, HashSet<string>> namesByKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            DateTime retainFrom = _TimeProvider.GetUtcNow().UtcDateTime - _ParticipantNameRetention;
            lock (_ParticipantNameLock)
            {
                foreach (KeyValuePair<string, ParticipantNameHistory> entry in _ParticipantNames)
                {
                    HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (KeyValuePair<string, DateTime> name in entry.Value.LastSeenByName)
                        if (name.Value >= retainFrom) names.Add(name.Key);
                    namesByKey[entry.Key] = names;
                }
            }

            List<CoordinationPresenceEntry> presence = await EnumeratePresenceAcrossRoomsAsync((int)_ParticipantNameRetention.TotalMinutes, token).ConfigureAwait(false);
            foreach (CoordinationPresenceEntry entry in presence)
            {
                HashSet<string>? names;
                if (!namesByKey.TryGetValue(entry.ParticipantKey, out names))
                {
                    names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    namesByKey[entry.ParticipantKey] = names;
                }
                if (!String.IsNullOrWhiteSpace(entry.DisplayName)) names.Add(entry.DisplayName.Trim());
            }

            if (namesByKey.ContainsKey(requested)) return resolution;

            List<string> matches = namesByKey.Where(e => e.Value.Contains(requested)).Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (matches.Count == 1)
            {
                resolution.ResolvedKey = matches[0];
                resolution.Notice = "'" + requested + "' is not a participant key; it is the name participant key '" + matches[0]
                    + "' posts or heartbeats under, so the note and its wake go to '" + matches[0]
                    + "'. Address notes to the participant key.";
            }
            else if (matches.Count > 1)
            {
                resolution.Notice = "'" + requested + "' is not a participant key, and several participant keys use it as a name ("
                    + String.Join(", ", matches) + "). The note stays addressed to '" + requested
                    + "' and reaches no session until one reads that key. Address the note to one of those keys.";
            }
            return resolution;
        }

        /// <summary>
        /// Participant keys in use under several names within the window. A key qualifies when it
        /// posted or heartbeated under two or more names and switched names at least twice, so one
        /// rename is not reported while two sessions alternating on one key are. Names are tracked
        /// in memory from every post and heartbeat since the admiral started, across all rooms.
        /// </summary>
        /// <param name="activeWithinMinutes">Presence window in minutes.</param>
        /// <returns>Shared keys, most recently seen first.</returns>
        public List<CoordinationSharedParticipantKey> EnumerateSharedParticipantKeys(int activeWithinMinutes = 15)
        {
            TimeSpan window = TimeSpan.FromMinutes(Math.Max(1, activeWithinMinutes));
            DateTime now = _TimeProvider.GetUtcNow().UtcDateTime;
            List<CoordinationSharedParticipantKey> shared = new List<CoordinationSharedParticipantKey>();
            lock (_ParticipantNameLock)
            {
                foreach (KeyValuePair<string, ParticipantNameHistory> entry in _ParticipantNames)
                {
                    CoordinationSharedParticipantKey? found = DescribeShared(entry.Key, entry.Value, now, window);
                    if (found != null) shared.Add(found);
                }
            }
            return shared.OrderByDescending(s => s.LastSeenUtc).ToList();
        }

        /// <summary>
        /// The shared-key finding for one participant key within the window, or null when the key
        /// is in use under one name.
        /// </summary>
        /// <param name="participantKey">Participant key.</param>
        /// <param name="activeWithinMinutes">Presence window in minutes.</param>
        /// <returns>The finding, or null.</returns>
        public CoordinationSharedParticipantKey? FindSharedParticipantKey(string participantKey, int activeWithinMinutes = 15)
        {
            if (String.IsNullOrWhiteSpace(participantKey)) return null;
            TimeSpan window = TimeSpan.FromMinutes(Math.Max(1, activeWithinMinutes));
            DateTime now = _TimeProvider.GetUtcNow().UtcDateTime;
            lock (_ParticipantNameLock)
            {
                ParticipantNameHistory? history;
                if (!_ParticipantNames.TryGetValue(participantKey, out history)) return null;
                return DescribeShared(participantKey, history, now, window);
            }
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
        /// Presence across every room: one entry per participant key active within the window,
        /// naming each room the participant was seen in, most recently active participant first.
        /// </summary>
        /// <param name="activeWithinMinutes">Activity window in minutes.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Aggregated presence entries.</returns>
        public async Task<List<CoordinationPresenceEntry>> EnumeratePresenceAcrossRoomsAsync(int activeWithinMinutes = 15, CancellationToken token = default)
        {
            List<CoordinationRoom> rooms = await EnumerateRoomsAsync(token).ConfigureAwait(false);
            Dictionary<string, CoordinationPresenceEntry> byKey = new Dictionary<string, CoordinationPresenceEntry>(StringComparer.Ordinal);
            Dictionary<string, List<CoordinationPresenceRoom>> roomsByKey = new Dictionary<string, List<CoordinationPresenceRoom>>(StringComparer.Ordinal);

            foreach (CoordinationRoom room in rooms)
            {
                List<CoordinationParticipant> participants = await _Database.CoordinationParticipants
                    .EnumerateByRoomAsync(room.Id, activeWithinMinutes, token).ConfigureAwait(false);
                foreach (CoordinationParticipant participant in participants)
                {
                    if (!byKey.TryGetValue(participant.ParticipantKey, out CoordinationPresenceEntry? entry))
                    {
                        entry = new CoordinationPresenceEntry
                        {
                            ParticipantKey = participant.ParticipantKey,
                            DisplayName = participant.DisplayName,
                            LastSeenUtc = participant.LastSeenUtc
                        };
                        byKey[participant.ParticipantKey] = entry;
                        roomsByKey[participant.ParticipantKey] = new List<CoordinationPresenceRoom>();
                    }
                    else if (participant.LastSeenUtc > entry.LastSeenUtc)
                    {
                        entry.DisplayName = participant.DisplayName;
                        entry.LastSeenUtc = participant.LastSeenUtc;
                    }

                    roomsByKey[participant.ParticipantKey].Add(new CoordinationPresenceRoom
                    {
                        RoomKey = room.Key,
                        RoomName = room.Name,
                        LastSeenUtc = participant.LastSeenUtc
                    });
                }
            }

            foreach (KeyValuePair<string, CoordinationPresenceEntry> pair in byKey)
            {
                pair.Value.Rooms = roomsByKey[pair.Key].OrderByDescending(r => r.LastSeenUtc).ToList();
            }

            return byKey.Values.OrderByDescending(e => e.LastSeenUtc).ToList();
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
        /// Create a work reservation and announce it on the board.
        /// </summary>
        /// <param name="participantKey">Stable participant key of the holder.</param>
        /// <param name="displayName">Display name of the holder.</param>
        /// <param name="subjectType">What is being reserved.</param>
        /// <param name="subjectId">Identifier of the reserved record.</param>
        /// <param name="note">Free-text note about the intended work.</param>
        /// <param name="ttlHours">Hours until the claim lapses without a heartbeat.</param>
        /// <param name="roomKey">Room key. When omitted, a claim on an objective in a campaign lands in that campaign's room and every other claim in the fleet room.</param>
        /// <param name="tenantId">Optional tenant identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created claim.</returns>
        public async Task<CoordinationClaim> ClaimAsync(
            string participantKey,
            string displayName,
            CoordinationClaimSubjectEnum subjectType,
            string subjectId,
            string? note = null,
            double ttlHours = 4,
            string? roomKey = null,
            string? tenantId = null,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(participantKey)) throw new ArgumentException("Participant key must not be empty.", nameof(participantKey));
            if (String.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Display name must not be empty.", nameof(displayName));
            if (String.IsNullOrWhiteSpace(subjectId)) throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
            if (ttlHours < 0.5) ttlHours = 0.5;
            if (ttlHours > 72) ttlHours = 72;

            string key = await ResolveRoomKeyAsync(
                roomKey,
                subjectType == CoordinationClaimSubjectEnum.Objective ? subjectId : null,
                token).ConfigureAwait(false);
            CoordinationRoom room = await EnsureRoomAsync(key, DefaultRoomKey == key ? DefaultRoomName : null, DefaultRoomKey == key ? DefaultRoomDescription : null, token).ConfigureAwait(false);

            CoordinationClaim claim = new CoordinationClaim
            {
                CoordinationRoomId = room.Id,
                TenantId = tenantId,
                ParticipantKey = participantKey,
                DisplayName = displayName,
                SubjectType = subjectType,
                SubjectId = subjectId,
                Note = note,
                Status = CoordinationClaimStatusEnum.Active,
                ExpiresUtc = DateTime.UtcNow.AddHours(ttlHours)
            };
            claim = await _Database.CoordinationClaims.CreateAsync(claim, token).ConfigureAwait(false);

            string subjectLabel = subjectType.ToString().ToLowerInvariant() + " " + subjectId;
            await PostMessageAsync(
                key,
                CoordinationAuthorTypeEnum.System,
                null,
                "armada",
                "[claim] " + displayName + " claimed " + subjectLabel +
                (String.IsNullOrWhiteSpace(note) ? String.Empty : ": " + note) +
                " (expires in " + ttlHours.ToString("0.#") + "h unless refreshed)",
                null, null,
                subjectType == CoordinationClaimSubjectEnum.Vessel ? subjectId : null,
                null,
                tenantId,
                toParticipantKey: null,
                token).ConfigureAwait(false);

            return claim;
        }

        /// <summary>
        /// Release a claim by identifier.
        /// </summary>
        /// <param name="claimId">Claim identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The released claim, or null when not found.</returns>
        public async Task<CoordinationClaim?> ReleaseClaimAsync(string claimId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(claimId)) throw new ArgumentException("Claim id must not be empty.", nameof(claimId));

            CoordinationClaim? claim = await _Database.CoordinationClaims.ReadAsync(claimId, token).ConfigureAwait(false);
            if (claim == null || claim.Status != CoordinationClaimStatusEnum.Active) return claim;

            claim.Status = CoordinationClaimStatusEnum.Released;
            claim = await _Database.CoordinationClaims.UpdateAsync(claim, token).ConfigureAwait(false);

            try
            {
                CoordinationRoom? room = await _Database.CoordinationRooms.ReadAsync(claim.CoordinationRoomId, token).ConfigureAwait(false);
                await PostMessageAsync(
                    room?.Key ?? DefaultRoomKey,
                    CoordinationAuthorTypeEnum.System,
                    null,
                    "armada",
                    "[claim] " + claim.DisplayName + " released the claim on " +
                    claim.SubjectType.ToString().ToLowerInvariant() + " " + claim.SubjectId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to announce claim release: " + ex.Message);
            }

            return claim;
        }

        /// <summary>
        /// Enumerate active claims, optionally narrowed to one subject.
        /// </summary>
        /// <param name="subjectType">Optional subject type filter.</param>
        /// <param name="subjectId">Optional subject identifier filter.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Active claims oldest first.</returns>
        public async Task<List<CoordinationClaim>> EnumerateActiveClaimsAsync(
            CoordinationClaimSubjectEnum? subjectType = null,
            string? subjectId = null,
            CancellationToken token = default)
        {
            return await _Database.CoordinationClaims.EnumerateActiveAsync(subjectType, subjectId, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Find active claims that conflict with an intended dispatch on a vessel
        /// or objective. Claims held by the requesting participant are excluded.
        /// </summary>
        /// <param name="vesselId">Target vessel identifier.</param>
        /// <param name="objectiveId">Optional target objective identifier.</param>
        /// <param name="exceptParticipantKey">Optional participant key to exclude, usually the dispatcher.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Conflicting active claims.</returns>
        public async Task<List<CoordinationClaim>> FindDispatchConflictsAsync(
            string vesselId,
            string? objectiveId = null,
            string? exceptParticipantKey = null,
            CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(vesselId)) return new List<CoordinationClaim>();

            List<CoordinationClaim> conflicts = new List<CoordinationClaim>();
            List<CoordinationClaim> candidates = await _Database.CoordinationClaims.EnumerateActiveAsync(null, null, token).ConfigureAwait(false);
            foreach (CoordinationClaim claim in candidates)
            {
                if (!String.IsNullOrEmpty(exceptParticipantKey) &&
                    String.Equals(claim.ParticipantKey, exceptParticipantKey, StringComparison.Ordinal))
                {
                    continue;
                }

                bool vesselConflict = claim.SubjectType == CoordinationClaimSubjectEnum.Vessel &&
                    String.Equals(claim.SubjectId, vesselId, StringComparison.Ordinal);
                bool objectiveConflict = !String.IsNullOrEmpty(objectiveId) &&
                    claim.SubjectType == CoordinationClaimSubjectEnum.Objective &&
                    String.Equals(claim.SubjectId, objectiveId, StringComparison.Ordinal);

                if (vesselConflict || objectiveConflict) conflicts.Add(claim);
            }

            return conflicts;
        }

        /// <summary>
        /// Copy a mirrored fleet note into the campaign room of the voyage it concerns. The voyage
        /// is the given one, or the mission's voyage when only a mission is named. The copy carries
        /// no voyage id: voyage-tagged notes reach captain briefs whatever room they sit in, and the
        /// fleet room already holds the voyage-tagged original, so a tagged copy would repeat it in
        /// the brief.
        /// </summary>
        /// <param name="content">Mirrored note content.</param>
        /// <param name="voyageId">Voyage identifier, when the event names one.</param>
        /// <param name="missionId">Mission identifier, when the event names one.</param>
        /// <param name="vesselId">Vessel identifier, when the event names one.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The campaign-room copy, or null when the voyage is not in a campaign.</returns>
        public async Task<CoordinationMessage?> PostCampaignMirrorAsync(
            string content,
            string? voyageId,
            string? missionId,
            string? vesselId,
            CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(content)) return null;

            string? resolvedVoyageId = voyageId;
            if (String.IsNullOrWhiteSpace(resolvedVoyageId) && !String.IsNullOrWhiteSpace(missionId))
            {
                Mission? mission = await _Database.Missions.ReadAsync(missionId!, token).ConfigureAwait(false);
                resolvedVoyageId = mission?.VoyageId;
            }

            if (String.IsNullOrWhiteSpace(resolvedVoyageId)) return null;

            string? campaignKey = await ResolveCampaignRoomKeyForVoyageAsync(resolvedVoyageId!, token).ConfigureAwait(false);
            if (campaignKey == null) return null;

            return await PostMessageAsync(
                campaignKey,
                CoordinationAuthorTypeEnum.System,
                null,
                "armada",
                content,
                null,
                missionId,
                vesselId,
                null,
                null,
                token: token).ConfigureAwait(false);
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

        /// <summary>
        /// The campaign room key for a voyage, remembered for a few minutes: a voyage reports one
        /// mirrored event per stage, and resolving it reads every objective. A null answer (the
        /// voyage serves no campaign objective) is remembered only briefly, because an objective can
        /// link the voyage just after it is dispatched.
        /// </summary>
        private async Task<string?> ResolveCampaignRoomKeyForVoyageAsync(string voyageId, CancellationToken token)
        {
            DateTime now = DateTime.UtcNow;
            lock (_CampaignVoyageCacheLock)
            {
                if (_CampaignVoyageCache.TryGetValue(voyageId, out CampaignVoyageCacheEntry? cached)
                    && now - cached.ResolvedUtc < (cached.RoomKey == null ? _NoCampaignVoyageCacheLifetime : _CampaignVoyageCacheLifetime))
                {
                    return cached.RoomKey;
                }
            }

            string? roomKey = await CampaignRoomResolver.ResolveRoomKeyForVoyageAsync(_Database, voyageId, token).ConfigureAwait(false);
            lock (_CampaignVoyageCacheLock)
            {
                if (_CampaignVoyageCache.Count >= _MaximumCampaignVoyageCacheEntries) _CampaignVoyageCache.Clear();
                _CampaignVoyageCache[voyageId] = new CampaignVoyageCacheEntry { RoomKey = roomKey, ResolvedUtc = now };
            }

            return roomKey;
        }

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

        private CoordinationSharedParticipantKey? RecordParticipantName(string participantKey, string displayName)
        {
            string name = displayName.Trim();
            DateTime now = _TimeProvider.GetUtcNow().UtcDateTime;
            lock (_ParticipantNameLock)
            {
                ParticipantNameHistory? history;
                if (!_ParticipantNames.TryGetValue(participantKey, out history))
                {
                    if (_ParticipantNames.Count >= _MaximumTrackedParticipantKeys) PruneParticipantNames(now);
                    history = new ParticipantNameHistory();
                    _ParticipantNames[participantKey] = history;
                }

                DateTime retainFrom = now - _ParticipantNameRetention;
                foreach (string stale in history.LastSeenByName.Where(n => n.Value < retainFrom).Select(n => n.Key).ToList())
                    history.LastSeenByName.Remove(stale);
                history.SwitchUtcs.RemoveAll(t => t < retainFrom);

                if (history.LastName != null && !String.Equals(history.LastName, name, StringComparison.Ordinal))
                    history.SwitchUtcs.Add(now);
                history.LastName = name;
                history.LastSeenByName[name] = now;

                CoordinationSharedParticipantKey? shared = DescribeShared(participantKey, history, now, _SharedKeyWarningWindow);
                if (shared == null) return null;
                if (history.WarnedUtc.HasValue && now - history.WarnedUtc.Value < _SharedKeyWarningWindow) return null;
                history.WarnedUtc = now;
                return shared;
            }
        }

        private void PruneParticipantNames(DateTime now)
        {
            DateTime retainFrom = now - _ParticipantNameRetention;
            foreach (string key in _ParticipantNames
                .Where(e => e.Value.LastSeenByName.Count == 0 || e.Value.LastSeenByName.Values.Max() < retainFrom)
                .Select(e => e.Key).ToList())
            {
                _ParticipantNames.Remove(key);
            }
        }

        private static CoordinationSharedParticipantKey? DescribeShared(string participantKey, ParticipantNameHistory history, DateTime now, TimeSpan window)
        {
            DateTime from = now - window;
            List<KeyValuePair<string, DateTime>> names = history.LastSeenByName
                .Where(n => n.Value >= from)
                .OrderByDescending(n => n.Value)
                .ToList();
            int switches = history.SwitchUtcs.Count(t => t >= from);
            if (names.Count < 2 || switches < 2) return null;

            List<string> displayNames = names.Select(n => n.Key).ToList();
            return new CoordinationSharedParticipantKey
            {
                ParticipantKey = participantKey,
                DisplayNames = displayNames,
                NameSwitches = switches,
                LastSeenUtc = names[0].Value,
                Reason = "Participant key " + participantKey + " posted or heartbeated as " + String.Join(", ", displayNames)
                    + " within " + (int)window.TotalMinutes + " minutes and switched names " + switches
                    + " times. Several sessions appear to share it: an addressed note or wake to this key reaches all of them,"
                    + " and the presence row shows only the name that wrote last. Give each session its own participant key."
            };
        }

        private async Task RecordSharedParticipantKeyEventAsync(CoordinationSharedParticipantKey shared, string? tenantId, CancellationToken token)
        {
            _Logging.Warn(_Header + shared.Reason);
            try
            {
                ArmadaEvent evt = new ArmadaEvent(SharedParticipantKeyEventType, shared.Reason);
                evt.TenantId = tenantId;
                evt.EntityType = "coordination_participant";
                evt.EntityId = shared.ParticipantKey;
                evt.Payload = System.Text.Json.JsonSerializer.Serialize(shared);
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to record the shared participant key event for " + shared.ParticipantKey + ": " + ex.Message);
            }
        }

        #endregion

        /// <summary>
        /// Names one participant key used recently, the order they switched in, and when the key was
        /// last reported as shared.
        /// </summary>
        private sealed class ParticipantNameHistory
        {
            /// <summary>Last post or heartbeat time per name.</summary>
            public Dictionary<string, DateTime> LastSeenByName { get; } = new Dictionary<string, DateTime>(StringComparer.Ordinal);

            /// <summary>Name on the most recent post or heartbeat.</summary>
            public string? LastName { get; set; } = null;

            /// <summary>Times the name differed from the previous post or heartbeat.</summary>
            public List<DateTime> SwitchUtcs { get; } = new List<DateTime>();

            /// <summary>When the shared-key event was last written for this key.</summary>
            public DateTime? WarnedUtc { get; set; } = null;
        }

        /// <summary>
        /// A remembered voyage-to-campaign-room answer.
        /// </summary>
        private sealed class CampaignVoyageCacheEntry
        {
            /// <summary>Campaign room key, or null when the voyage serves no campaign objective.</summary>
            public string? RoomKey { get; set; } = null;

            /// <summary>When the answer was resolved, in UTC.</summary>
            public DateTime ResolvedUtc { get; set; } = DateTime.UtcNow;
        }
    }
}
