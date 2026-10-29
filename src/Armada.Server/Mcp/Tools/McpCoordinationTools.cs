namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Services;
    using Armada.Core.Models;
    using ArmadaConstants = Armada.Core.Constants;

    /// <summary>
    /// Registers MCP tools for the shared coordination board (chatroom) that keeps
    /// concurrent operator sessions aware of each other's work, plus the dispatch
    /// hold that stops new voyages while Armada itself is being worked on.
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
        /// <param name="coordination">Optional coordination service.</param>
        /// <param name="dispatchHold">Optional dispatch hold shared with the admiral's dispatch paths.</param>
        /// <param name="triageAdapter">Optional D11 <c>inbox_triage</c> adapter. When set and in Gate mode it annotates each board note with an <c>attention</c> label and a <c>noteKind</c> and sorts the read by attention; it never hides a note. Null or Off leaves the deterministic order.</param>
        /// <param name="jobs">Optional background job service. When set, hold status and engage list the jobs accepted but not finished, which a restart would record as Lost.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, CoordinationService? coordination, Armada.Core.Services.DispatchHold? dispatchHold = null, Armada.Core.Services.InboxTriageAdapter? triageAdapter = null, LongRunningJobService? jobs = null)
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
                        authorName = new { type = "string", description = "Optional display name for the note, for example 'claude-session-1'. Omit it to post under your session's identity: your participant's board name (from the X-Armada-Participant header or authorId), else the name of the credential you authenticated with. A value you pass wins." },
                        authorId = new { type = "string", description = "Optional stable identifier for your session. Defaults to authorName when omitted." },
                        roomKey = new { type = "string", description = "Usually omit this: every session shares the fleet board, and a note naming an objectiveId in a campaign routes to that campaign's room by itself. Set it only for a deliberately separate room, for example campaign-<name>; the words fleet and default both mean the shared board." },
                        objectiveId = new { type = "string", description = "Optional objective ID (obj_ prefix) the note concerns. When roomKey is omitted and the objective belongs to a campaign (an ancestor tagged campaign:<name>), the note lands in that campaign's room, created on first use; otherwise in the fleet room." },
                        voyageId = new { type = "string", description = "Optional related voyage ID (vyg_ prefix)." },
                        missionId = new { type = "string", description = "Optional related mission ID (msn_ prefix)." },
                        vesselId = new { type = "string", description = "Optional related vessel ID (vsl_ prefix)." },
                        incidentId = new { type = "string", description = "Optional related incident ID (inc_ prefix)." },
                        toParticipantKey = new { type = "string", description = "Optional participant key to address this note to - marks it as work or an answer directed at one session while staying visible to all. A value that is no participant key but is a name a participant key posted or heartbeated under in the last 24 hours is delivered to that key; a name several keys use is delivered to each of them. The result's AddressNotice names every key the note reaches; address notes to the key." }
                    },
                    required = new[] { "content" }
                },
                async (args) =>
                {
                    CoordinationPostArgs request = JsonSerializer.Deserialize<CoordinationPostArgs>(args!.Value, _JsonOptions)!;
                    if (String.IsNullOrWhiteSpace(request.Content))
                        return (object)new { Error = "content is required and must not be empty" };
                    if (coordination == null)
                        return McpToolUnavailable.Create("coordination board");
                    string? authenticatedParticipant = ArmadaMcpHttpServer.CurrentParticipantKey;
                    string? authorName = await coordination.ResolveCallerNameAsync(
                        request.AuthorName,
                        !String.IsNullOrWhiteSpace(authenticatedParticipant) ? authenticatedParticipant : request.AuthorId,
                        McpCallerContext.Current?.PrincipalDisplay).ConfigureAwait(false);
                    if (String.IsNullOrWhiteSpace(authorName))
                        return (object)new { Error = "authorName is required: pass authorName (the name to post under) or authorId, or send the " + ArmadaMcpHttpServer.ParticipantHeaderName + " header. This call carries no participant key and its credential has no name." };

                    string roomKey = await coordination.ResolveRoomKeyAsync(request.RoomKey, request.ObjectiveId).ConfigureAwait(false);
                    string authorId = !String.IsNullOrWhiteSpace(authenticatedParticipant)
                        ? authenticatedParticipant!
                        : (String.IsNullOrWhiteSpace(request.AuthorId) ? authorName! : request.AuthorId!);

                    try
                    {
                        CoordinationMessage message = await coordination.PostMessageAsync(
                            roomKey,
                            CoordinationAuthorTypeEnum.Operator,
                            authorId,
                            authorName!,
                            request.Content!,
                            request.VoyageId,
                            request.MissionId,
                            request.VesselId,
                            request.IncidentId,
                            ArmadaConstants.DefaultTenantId,
                            request.ToParticipantKey).ConfigureAwait(false);
                        return (object)message;
                    }
                    catch (NotSupportedException ex)
                    {
                        return (object)new { Error = ex.Message };
                    }
                });

            register(
                "armada_coordination_read",
                "Read recent notes from the shared coordination board plus who is currently active. Read this before dispatching voyages or touching incidents so you do not duplicate another session's work. When called with participantKey and the response contains UnreadWakes, PAUSE what you are doing and address those messages first - they are directed work or answers waiting on you. Acknowledge each with armada_mark_signal_read. Long BROADCAST notes come back previewed at 600 characters, each carrying ContentLength and Truncated, with TruncatedMessageCount and a TruncationHint on the response; notes addressed to you (your participant key, or a name you posted or heartbeated under in the last 24 hours) are always whole. An UnreadWake carries the note's id as [msg=<id>] and at most the first 500 characters of the note. Pass messageId to read one note whole, or includeFullContent=true, ideally with a smaller limit or an afterUtc, to read every note whole. SharedParticipantKeys lists participant keys that posted or heartbeated under several names within the presence window and switched back and forth: several sessions share each such key, so an addressed note or wake to it reaches all of them and its presence row shows only the last name. Give each session its own key.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        roomKey = new { type = "string", description = "Omit this for the shared fleet board. Pass campaign-<name> to read a campaign's room, where that campaign's claims, notes and voyage events land; the words fleet and default both mean the shared board." },
                        limit = new { type = "integer", description = "Maximum number of notes to return (default 50)." },
                        afterUtc = new { type = "string", description = "Optional ISO 8601 timestamp; returns only notes created after it." },
                        activeWithinMinutes = new { type = "integer", description = "Presence window in minutes (default 15)." },
                        participantKey = new { type = "string", description = "Your session participant key. When supplied, notes are filtered to broadcast plus notes addressed to you - how a joining session picks up work handed to it. A session that sends the X-Armada-Participant header reads as that key, and the result names it as YourParticipantKey; passing a different key is refused rather than silently read as the header key. The configured key a bridge made unique for this session, and a name this session posted or heartbeated under in the last 24 hours, are accepted as the header key. Notes addressed to such a name are your mail: the read returns them, whole." },
                        includeFullContent = new { type = "boolean", description = "Return every note whole instead of previewing long broadcast notes (default false). Notes addressed to you are always whole either way." },
                        messageId = new { type = "string", description = "Optional note id (the Id of a board note, or the [msg=<id>] a wake names). Returns that one note whole as Message, with your UnreadWakes; the other filters are ignored." }
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
                    string? keyRefusal = await HeaderParticipantKeyRefusalAsync(coordination, request.ParticipantKey).ConfigureAwait(false);
                    if (keyRefusal != null)
                        return (object)new { Error = keyRefusal };
                    if (coordination == null)
                        return McpToolUnavailable.Create("coordination board");
                    string? participantKey = ArmadaMcpHttpServer.CurrentParticipantKey ?? request.ParticipantKey;

                    try
                    {
                        HashSet<string> callerAddresses = await CallerAddressesAsync(coordination, participantKey).ConfigureAwait(false);
                        if (!String.IsNullOrWhiteSpace(request.MessageId))
                        {
                            CoordinationMessage? single = await database.CoordinationMessages.ReadAsync(request.MessageId!.Trim()).ConfigureAwait(false);
                            if (single == null)
                                return (object)new { Error = "No board note has id '" + request.MessageId!.Trim() + "'." };
                            List<Signal> singleWakes = new List<Signal>();
                            if (!String.IsNullOrWhiteSpace(participantKey))
                            {
                                try { singleWakes = await coordination.EnumerateUnreadWakesAsync(participantKey!).ConfigureAwait(false); }
                                catch (NotSupportedException) { }
                            }
                            return (object)new
                            {
                                YourParticipantKey = String.IsNullOrWhiteSpace(participantKey) ? null : participantKey,
                                Message = BuildMessageViews(new List<CoordinationMessage> { single }, callerAddresses, true)[0],
                                UnreadWakes = singleWakes
                            };
                        }

                        List<CoordinationMessage> messages = await coordination.ReadMessagesAsync(
                            roomKey, request.AfterUtc, limit,
                            token: System.Threading.CancellationToken.None,
                            visibleToParticipantKey: String.IsNullOrWhiteSpace(participantKey) ? null : participantKey).ConfigureAwait(false);

                        List<Signal> unreadWakes = new List<Signal>();
                        if (!String.IsNullOrWhiteSpace(participantKey))
                        {
                            try { unreadWakes = await coordination.EnumerateUnreadWakesAsync(participantKey!).ConfigureAwait(false); }
                            catch (NotSupportedException) { }
                        }
                        List<CoordinationParticipant> participants = await coordination.EnumerateParticipantsAsync(roomKey, activeWithinMinutes).ConfigureAwait(false);
                        List<CoordinationClaim> claims;
                        try
                        {
                            claims = await coordination.EnumerateActiveClaimsAsync(null, null).ConfigureAwait(false);
                        }
                        catch (NotSupportedException)
                        {
                            claims = new List<CoordinationClaim>();
                        }

                        List<CoordinationMessageView> views = BuildMessageViews(
                            messages, callerAddresses, request.IncludeFullContent == true);
                        int truncatedCount = views.Count(view => view.Truncated);

                        // D11 inbox_triage: annotate each note with attention and kind and sort by
                        // attention in Gate mode. It never hides a note, so with no adapter or the
                        // decision Off the deterministic order stands.
                        views = await ApplyBoardNoteTriageAsync(views, triageAdapter, database).ConfigureAwait(false);

                        return (object)new
                        {
                            RoomKey = roomKey,
                            YourParticipantKey = String.IsNullOrWhiteSpace(participantKey) ? null : participantKey,
                            Messages = views,
                            TruncatedMessageCount = truncatedCount,
                            TruncationHint = truncatedCount == 0
                                ? null
                                : truncatedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + " broadcast note(s) previewed at " + _BOARD_PREVIEW_CHARS.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    + " characters (Truncated=true). Read one whole with messageId=<Id>, or every note whole with includeFullContent=true.",
                            ActiveParticipants = participants,
                            SharedParticipantKeys = coordination.EnumerateSharedParticipantKeys(activeWithinMinutes),
                            ActiveClaims = claims,
                            UnreadWakes = unreadWakes
                        };
                    }
                    catch (NotSupportedException ex)
                    {
                        return (object)new { Error = ex.Message };
                    }
                });

            register(
                "armada_coordination_heartbeat",
                "Send a presence heartbeat to the shared coordination board. Call periodically while working so other sessions can see you are active, and to prune stale presence. The response carries UnreadWakes: when non-empty, PAUSE your current work and address those directed messages first, then acknowledge each with armada_mark_signal_read. SharedParticipantKey is set when your key posted or heartbeated under several names within the presence window: another session uses the same key, so wakes addressed to it reach both of you. Move one session to its own key.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        participantKey = new { type = "string", description = "Stable identifier for your session. Omit it when the session sends the X-Armada-Participant header: the header key is used, and the result names it as YourParticipantKey. When given with the header it must equal the header key, the configured key the bridge made unique, or a name this session posted or heartbeated under in the last 24 hours." },
                        displayName = new { type = "string", description = "Display name shown to other sessions. Omit it to keep your participant's last board name, else the name of the credential you authenticated with, else the participant key." },
                        roomKey = new { type = "string", description = "Omit this: every session shares one board. Only set it for a deliberately separate room; the words fleet and default both mean the shared board." }
                    }
                },
                async (args) =>
                {
                    CoordinationHeartbeatArgs request = JsonSerializer.Deserialize<CoordinationHeartbeatArgs>(args!.Value, _JsonOptions)!;
                    string? keyRefusal = await HeaderParticipantKeyRefusalAsync(coordination, request.ParticipantKey).ConfigureAwait(false);
                    if (keyRefusal != null)
                        return (object)new { Error = keyRefusal };
                    string? participantKey = ArmadaMcpHttpServer.CurrentParticipantKey ?? request.ParticipantKey;
                    if (String.IsNullOrWhiteSpace(participantKey))
                        return (object)new { Error = "participantKey is required when the session sends no " + ArmadaMcpHttpServer.ParticipantHeaderName + " header" };
                    if (coordination == null)
                        return McpToolUnavailable.Create("coordination board");
                    string? displayName = await coordination.ResolveCallerNameAsync(
                        request.DisplayName, participantKey, McpCallerContext.Current?.PrincipalDisplay).ConfigureAwait(false);

                    string roomKey = String.IsNullOrWhiteSpace(request.RoomKey) ? CoordinationService.DefaultRoomKey : request.RoomKey!;

                    try
                    {
                        CoordinationParticipant participant = await coordination.HeartbeatAsync(
                            roomKey,
                            participantKey!,
                            displayName!,
                            ArmadaConstants.DefaultTenantId).ConfigureAwait(false);

                        List<Signal> unreadWakes = new List<Signal>();
                        try { unreadWakes = await coordination.EnumerateUnreadWakesAsync(participantKey!).ConfigureAwait(false); }
                        catch (NotSupportedException) { }

                        return (object)new { Participant = participant, YourParticipantKey = participantKey, UnreadWakes = unreadWakes, SharedParticipantKey = coordination.FindSharedParticipantKey(participantKey!) };
                    }
                    catch (NotSupportedException ex)
                    {
                        return (object)new { Error = ex.Message };
                    }
                });

            register(
                "armada_coordination_claim",
                "Reserve work so other operator sessions do not dispatch the same thing. Claims are visible to everyone, expire after a few hours unless your heartbeats keep them alive, and dispatches against a claimed vessel or objective announce the overlap on the board. Actions: claim (subjectType + subjectId required), release (claimId), list (optional subjectType + subjectId). action is required: a missing or unknown action returns an error (missing_action for a blank one), never a silent empty claim list.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { type = "string", description = "claim | release | list. Required: a missing or unknown value returns an error, never a silent empty claim list." },
                        subjectType = new { type = "string", description = "vessel | objective. Required for claim." },
                        subjectId = new { type = "string", description = "The vsl_ or obj_ identifier to reserve. Required for claim." },
                        note = new { type = "string", description = "What you intend to do with the subject." },
                        participantKey = new { type = "string", description = "Stable key for your session. Required for claim when the session sends no X-Armada-Participant header; the header key is used when it does." },
                        displayName = new { type="string", description = "Display name for the board. Omit it to use your participant's last board name, else the name of the credential you authenticated with, else the participant key." },
                        ttlHours = new { type = "number", description = "Hours until expiry without heartbeat. Default 4, clamped 0.5-72." },
                        claimId = new { type = "string", description = "Claim ID (ccl_ prefix). Required for release." },
                        roomKey = new { type = "string", description = "Usually omit this. A claim on an objective in a campaign (an ancestor tagged campaign:<name>) lands in that campaign's room by itself; every other claim lands in the fleet room. Heartbeats in any room keep every claim you hold alive." }
                    },
                    required = new[] { "action" }
                },
                async (args) =>
                {
                    ClaimArgs request = JsonSerializer.Deserialize<ClaimArgs>(args!.Value, _JsonOptions)!;
                    // A missing or blank action is an explicit error, never a silent default to a read.
                    // The action argument is required; a caller who sends subject fields but omits the action
                    // means to claim, and must not read an empty claim list as a successful reservation.
                    if (String.IsNullOrWhiteSpace(request.Action))
                        return (object)new { Error = "missing_action: action is required - pass claim, release, or list" };
                    string action = request.Action!.Trim().ToLowerInvariant();
                    string? participantKey = ArmadaMcpHttpServer.CurrentParticipantKey ?? request.ParticipantKey;

                    try
                    {
                        if (String.Equals(action, "claim", StringComparison.Ordinal))
                        {
                            if (String.IsNullOrWhiteSpace(request.SubjectId) || String.IsNullOrWhiteSpace(request.SubjectType))
                                return (object)new { Error = "subjectType and subjectId are required for action=claim" };
                            CoordinationClaimSubjectEnum subjectType;
                            if (!Enum.TryParse(request.SubjectType!, true, out subjectType))
                                return (object)new { Error = "subjectType must be vessel or objective" };
                            if (String.IsNullOrWhiteSpace(participantKey))
                                return (object)new { Error = "participantKey is required for action=claim when the session sends no " + ArmadaMcpHttpServer.ParticipantHeaderName + " header - name your session" };
                            if (coordination == null)
                                return McpToolUnavailable.Create("coordination board");
                            string? claimDisplayName = await coordination.ResolveCallerNameAsync(
                                request.DisplayName, participantKey, McpCallerContext.Current?.PrincipalDisplay).ConfigureAwait(false);

                            CoordinationClaim claim = await coordination.ClaimAsync(
                                participantKey!, claimDisplayName!, subjectType, request.SubjectId!,
                                request.Note, request.TtlHours ?? 4, request.RoomKey).ConfigureAwait(false);
                            return (object)claim;
                        }

                        if (String.Equals(action, "release", StringComparison.Ordinal))
                        {
                            if (String.IsNullOrWhiteSpace(request.ClaimId))
                                return (object)new { Error = "claimId is required for action=release" };
                            CoordinationClaim? existing = await database.CoordinationClaims.ReadAsync(request.ClaimId!).ConfigureAwait(false);
                            if (existing != null
                                && !String.IsNullOrWhiteSpace(ArmadaMcpHttpServer.CurrentParticipantKey)
                                && !String.Equals(existing.ParticipantKey, participantKey, StringComparison.Ordinal))
                            {
                                return (object)new { Error = "An authenticated participant can release only its own claim." };
                            }
                            if (coordination == null)
                                return McpToolUnavailable.Create("coordination board");
                            CoordinationClaim? released = await coordination.ReleaseClaimAsync(request.ClaimId!).ConfigureAwait(false);
                            if (released == null) return (object)new { Status = "not_found", ClaimId = request.ClaimId! };
                            return (object)released;
                        }

                        if (String.Equals(action, "list", StringComparison.Ordinal))
                        {
                            CoordinationClaimSubjectEnum subjectTypeParsed = CoordinationClaimSubjectEnum.Vessel;
                            bool hasSubjectType = !String.IsNullOrWhiteSpace(request.SubjectType);
                            if (hasSubjectType && !Enum.TryParse(request.SubjectType!, true, out subjectTypeParsed))
                                return (object)new { Error = "subjectType must be vessel or objective" };
                            if (coordination == null)
                                return McpToolUnavailable.Create("coordination board");
                            CoordinationClaimSubjectEnum? subjectType = hasSubjectType ? subjectTypeParsed : null;

                            List<CoordinationClaim> claims = await coordination.EnumerateActiveClaimsAsync(
                                String.IsNullOrWhiteSpace(request.SubjectId) ? subjectType : subjectType,
                                request.SubjectId).ConfigureAwait(false);
                            return (object)new { Claims = claims };
                        }

                        return (object)new { Error = "action must be claim, release, or list" };
                    }
                    catch (NotSupportedException ex)
                    {
                        return (object)new { Error = ex.Message };
                    }
                });

            register(
                "armada_campaign_status",
                "One-call status for a campaign: the objective tree under a root (or every root carrying a tag), grouped by parent with statuses, plus active coordination claims, recent fleet-board notes, and recent notes from each root's campaign room (CampaignRooms). Use this to start a session or answer 'where does this campaign stand' without a pile of enumerations.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        tag = new { type = "string", description = "Campaign tag, for example 'campaign:porting'. Resolves every objective carrying it as a root. Omit when rootObjectiveId is supplied." },
                        rootObjectiveId = new { type = "string", description = "Campaign root objective ID (obj_ prefix). Omit when tag is supplied." },
                        noteLimit = new { type = "integer", description = "Recent board notes to include (default 10)." },
                        includeFullContent = new { type = "boolean", description = "Return long free-text fields whole instead of previewing them (default false). Previewed fields carry a companion <name>Length, and the response carries TruncatedFieldCount." },
                        includeSlices = new { type = "boolean", description = "Include the slice objectives under each lane (default false; each lane always carries sliceCount). The lane rollup is what fits in one response; ask for slices when you need them." }
                    }
                },
                async (args) =>
                {
                    CampaignStatusArgs request = args != null && args.HasValue
                        ? JsonSerializer.Deserialize<CampaignStatusArgs>(args.Value, _JsonOptions) ?? new CampaignStatusArgs()
                        : new CampaignStatusArgs();

                    if (coordination == null)
                        return McpToolUnavailable.Create("coordination board");

                    List<CoordinationRoom> rooms = await database.CoordinationRooms.EnumerateAsync().ConfigureAwait(false);
                    string fleetRoomKey = CoordinationService.DefaultRoomKey;

                    // Resolve roots, then two levels: roots -> lanes/programs -> slices.
                    List<Objective> allObjectives = await database.Objectives.EnumerateAsync().ConfigureAwait(false);
                    List<string> rootIds = new List<string>();
                    if (!String.IsNullOrWhiteSpace(request.RootObjectiveId))
                    {
                        rootIds.Add(request.RootObjectiveId!);
                    }
                    else if (!String.IsNullOrWhiteSpace(request.Tag))
                    {
                        foreach (Objective o in allObjectives)
                        {
                            if (o.Tags != null && o.Tags.Contains(request.Tag!, StringComparer.Ordinal)) rootIds.Add(o.Id);
                        }
                    }

                    if (rootIds.Count == 0)
                        return (object)new { Error = "no campaign roots resolved - supply tag or rootObjectiveId" };

                    HashSet<string> rootSet = new HashSet<string>(rootIds);
                    Dictionary<string, List<CampaignNode>> byParent = new Dictionary<string, List<CampaignNode>>();

                    void AddToParent(string parentId, Objective o)
                    {
                        if (!byParent.TryGetValue(parentId, out List<CampaignNode> list))
                        {
                            list = new List<CampaignNode>();
                            byParent[parentId] = list;
                        }

                        list.Add(CampaignNode.From(o));
                    }

                    var groupIds = new HashSet<string>();
                    foreach (Objective o in allObjectives)
                    {
                        if (String.IsNullOrEmpty(o.ParentObjectiveId)) continue;
                        if (!rootSet.Contains(o.ParentObjectiveId!)) continue;
                        AddToParent(o.ParentObjectiveId!, o);
                        groupIds.Add(o.Id);
                    }

                    // Second level: children of the level-one groups (lanes -> slices).
                    foreach (Objective o in allObjectives)
                    {
                        if (String.IsNullOrEmpty(o.ParentObjectiveId)) continue;
                        if (!groupIds.Contains(o.ParentObjectiveId!)) continue;
                        AddToParent(o.ParentObjectiveId!, o);
                    }

                    bool includeSlices = args.HasValue
                        && args.Value.TryGetProperty("includeSlices", out JsonElement _slices)
                        && _slices.ValueKind == JsonValueKind.True;

                    var tree = new List<object>();
                    foreach (string rootId in rootIds)
                    {
                        Objective? root = allObjectives.FirstOrDefault(o => o.Id == rootId);
                        var lanes = new List<object>();
                        if (byParent.TryGetValue(rootId, out List<CampaignNode>? laneNodes))
                        {
                            foreach (CampaignNode lane in laneNodes)
                            {
                                byParent.TryGetValue(lane.Id, out List<CampaignNode>? slices);
                                List<CampaignNode> laneSlices = slices ?? new List<CampaignNode>();

                                // A campaign rollup is about lanes; the slices under them are
                                // most of the payload. One live campaign held 161 nodes at
                                // 102,096 characters, past the caller's limit, while the
                                // lane-level view is what the tool is actually for. Slices are
                                // therefore counted by default and returned on request.
                                if (includeSlices)
                                    lanes.Add(new { objective = lane, children = laneSlices, sliceCount = laneSlices.Count });
                                else
                                    lanes.Add(new { objective = lane, sliceCount = laneSlices.Count });
                            }
                        }

                        // Project the root the same way as its lanes. Serializing the full
                        // Objective here was most of the payload: 24 roots at 1.4 to 4.8 KB
                        // each, roughly 70 KB, against lanes of a few hundred bytes. A
                        // rollup wants the same summary at every level.
                        tree.Add(new
                        {
                            objective = root == null ? null : CampaignNode.From(root),
                            children = lanes
                        });
                    }

                    List<CoordinationClaim> claims;
                    try
                    {
                        claims = await database.CoordinationClaims.EnumerateActiveAsync(null, null).ConfigureAwait(false);
                    }
                    catch (NotSupportedException)
                    {
                        claims = new List<CoordinationClaim>();
                    }

                    int noteLimit = request.NoteLimit.HasValue && request.NoteLimit.Value > 0 ? Math.Min(request.NoteLimit.Value, 50) : 10;
                    List<CoordinationMessage> notes = new List<CoordinationMessage>();
                    try
                    {
                        CoordinationService coordinationService = new CoordinationService(new SyslogLogging.LoggingModule { Settings = { EnableConsole = false } }, database);
                        notes = await coordinationService.ReadMessagesAsync(fleetRoomKey, null, noteLimit).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Board notes are best-effort in a status call.
                    }

                    // Each campaign root's own room, when it exists, holds that campaign's claims,
                    // notes and voyage events; reading it does not create a room.
                    List<object> campaignRooms = new List<object>();
                    HashSet<string> campaignRoomKeys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string rootId in rootIds)
                    {
                        Objective? root = allObjectives.FirstOrDefault(o => o.Id == rootId);
                        string? campaignRoomKey = CampaignRoomResolver.BuildRoomKey(CampaignRoomResolver.ReadCampaignName(root));
                        if (campaignRoomKey == null || !campaignRoomKeys.Add(campaignRoomKey)) continue;
                        CoordinationRoom? campaignRoom = rooms.FirstOrDefault(r => String.Equals(r.Key, campaignRoomKey, StringComparison.Ordinal));
                        if (campaignRoom == null)
                        {
                            campaignRooms.Add(new { RoomKey = campaignRoomKey, Exists = false, RecentNotes = new List<CoordinationMessage>() });
                            continue;
                        }

                        try
                        {
                            List<CoordinationMessage> campaignNotes = await database.CoordinationMessages
                                .EnumerateByRoomAsync(campaignRoom.Id, null, noteLimit).ConfigureAwait(false);
                            campaignRooms.Add(new { RoomKey = campaignRoomKey, Exists = true, RecentNotes = campaignNotes });
                        }
                        catch (NotSupportedException ex)
                        {
                            campaignRooms.Add(new { RoomKey = campaignRoomKey, Exists = true, RecentNotes = new List<CoordinationMessage>(), Unavailable = ex.Message });
                        }
                    }

                    bool wantsFull = args.HasValue
                        && args.Value.TryGetProperty("includeFullContent", out JsonElement _full)
                        && _full.ValueKind == JsonValueKind.True;
                    return McpResultPreview.Apply(
                        new { Roots = rootIds, Tree = tree, ActiveClaims = claims, RecentNotes = notes, CampaignRooms = campaignRooms },
                        wantsFull);
                });

            {
                register(
                    "armada_dispatch_hold",
                    "Engage, clear, or inspect the fleet-wide dispatch hold. Engage it BEFORE rebuilding or redeploying the admiral so other sessions and the objective scheduler cannot start new voyages against a binary about to change. In-flight voyages continue; only new dispatches are refused. The hold is stored in the database, so it survives an admiral restart and stays engaged until you clear it. Engaging or clearing posts a system note to the coordination board. Status and engage list UnfinishedJobs: background jobs (dispatches included) accepted but not finished; RunningGates: definition-of-done gates running or waiting for the host command slot, each with a SlotWait (Running, WaitingForSlot with its Position in line, or Queued before its first command) so a queued gate is told from a lost one; GateReruns: interrupted gates being re-run; and HostSlot: who holds and who waits for the host command slot, with MemoryWaitReason when free memory holds the next request. A restart records each unfinished job Lost and kills every running gate and check, so wait for UnfinishedJobs, RunningGates and GateReruns to be empty and HostSlot to have no holder or waiter before restarting.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new { type = "string", description = "engage | clear | status" },
                            reason = new { type = "string", description = "Why the hold is engaged. Required for engage." },
                            setBy = new { type = "string", description = "Your session or operator name for engage. Omit it to use your participant's board name (X-Armada-Participant header), else the name of the credential you authenticated with. A value you pass wins." }
                        },
                        required = new[] { "action" }
                    },
                    async (args) =>
                    {
                        DispatchHoldArgs request = JsonSerializer.Deserialize<DispatchHoldArgs>(args!.Value, _JsonOptions)!;
                        string action = String.IsNullOrWhiteSpace(request.Action) ? "status" : request.Action!.Trim().ToLowerInvariant();

                        if (String.Equals(action, "status", StringComparison.Ordinal))
                        {
                            if (dispatchHold == null)
                                return McpToolUnavailable.Create("dispatch hold");
                            return (object)new { Active = dispatchHold.Snapshot() != null, Hold = dispatchHold.Snapshot(), UnfinishedJobs = UnfinishedJobs(jobs), RunningGates = RunningGates(), GateReruns = MissionService.GateRerunsInFlight(), HostSlot = HostWideCommandLock.Snapshot() };
                        }

                        if (String.Equals(action, "clear", StringComparison.Ordinal))
                        {
                            if (dispatchHold == null)
                                return McpToolUnavailable.Create("dispatch hold");
                            if (coordination == null)
                                return McpToolUnavailable.Create("coordination board");
                            bool wasActive = dispatchHold.Snapshot() != null;
                            await dispatchHold.ClearAsync().ConfigureAwait(false);
                            if (wasActive)
                            {
                                await SafePostAsync(coordination, "[hold] Dispatching resumed. The admiral is clear for new voyages.").ConfigureAwait(false);
                                await coordination.EmitHoldWakeAsync("[hold] Dispatching resumed - the admiral is clear for new voyages.").ConfigureAwait(false);
                            }

                            return (object)new { Active = false, Cleared = true };
                        }

                        if (String.Equals(action, "engage", StringComparison.Ordinal))
                        {
                            if (String.IsNullOrWhiteSpace(request.Reason))
                                return (object)new { Error = "reason is required when engaging the hold" };
                            if (dispatchHold == null)
                                return McpToolUnavailable.Create("dispatch hold");
                            if (coordination == null)
                                return McpToolUnavailable.Create("coordination board");
                            string? setBy = await coordination.ResolveCallerNameAsync(
                                request.SetBy, ArmadaMcpHttpServer.CurrentParticipantKey, McpCallerContext.Current?.PrincipalDisplay).ConfigureAwait(false);
                            if (String.IsNullOrWhiteSpace(setBy))
                                return (object)new { Error = "setBy is required when engaging the hold and the call carries no participant key or named credential - name your session so others know who to ask" };

                            await dispatchHold.EngageAsync(request.Reason!, setBy).ConfigureAwait(false);
                            await SafePostAsync(coordination, "[hold] Dispatching paused by " + setBy + ": " + request.Reason + " Hold off dispatching new voyages until this is cleared.").ConfigureAwait(false);
                            await coordination.EmitHoldWakeAsync("[hold] Dispatching paused by " + setBy + " (" + request.Reason + "). Hold new voyages; you will be woken on clear.").ConfigureAwait(false);
                            return (object)new { Active = true, Hold = dispatchHold.Snapshot(), UnfinishedJobs = UnfinishedJobs(jobs), RunningGates = RunningGates(), GateReruns = MissionService.GateRerunsInFlight(), HostSlot = HostWideCommandLock.Snapshot() };
                        }

                        return (object)new { Error = "action must be engage, clear, or status" };
                    });
            }
        }

        private static List<RunningGateRow> RunningGates()
        {
            HostSlotSnapshot slot = HostWideCommandLock.Snapshot();
            return DefinitionOfDoneGateRuns.Snapshot().Select(entry => new RunningGateRow
            {
                MissionId = entry.Key,
                StartedUtc = entry.Value,
                SlotWait = GateSlotWait.Describe(entry.Key, slot)
            }).ToList();
        }

        /// <summary>
        /// A definition-of-done gate running or waiting for the host command slot, as listed by the dispatch hold.
        /// </summary>
        public sealed class RunningGateRow
        {
            /// <summary>Mission whose gate runs.</summary>
            public string MissionId { get; set; } = String.Empty;

            /// <summary>When the gate started, including its wait for the host command slot.</summary>
            public DateTime StartedUtc { get; set; }

            /// <summary>
            /// Where the gate stands for the host command slot: Running while it holds one, WaitingForSlot with its
            /// Position while it waits in line, or Queued before its first command. This tells a queued gate from a
            /// lost one without reading its dock.
            /// </summary>
            public GateSlotWait? SlotWait { get; set; } = null;
        }

        private static List<UnfinishedJobRow> UnfinishedJobs(LongRunningJobService? jobs)
        {
            if (jobs == null) return new List<UnfinishedJobRow>();
            return jobs.ListUnfinished().Select(job => new UnfinishedJobRow
            {
                JobId = job.JobId,
                Operation = job.Operation,
                Status = job.Status.ToString(),
                SubmittedAtUtc = job.SubmittedAtUtc,
                StartedAtUtc = job.StartedAtUtc,
                ObjectiveId = job.ObjectiveId,
                VesselId = job.VesselId
            }).ToList();
        }

        /// <summary>
        /// A background job accepted but not finished, as listed by the dispatch hold.
        /// </summary>
        public sealed class UnfinishedJobRow
        {
            /// <summary>Job identifier.</summary>
            public string JobId { get; set; } = String.Empty;

            /// <summary>Operation name.</summary>
            public string Operation { get; set; } = String.Empty;

            /// <summary>Accepted or Running.</summary>
            public string Status { get; set; } = String.Empty;

            /// <summary>When the job was accepted.</summary>
            public DateTime SubmittedAtUtc { get; set; }

            /// <summary>When the job started running, if it has.</summary>
            public DateTime? StartedAtUtc { get; set; }

            /// <summary>Objective the job acts for, when known.</summary>
            public string? ObjectiveId { get; set; }

            /// <summary>Vessel the job acts on, when known.</summary>
            public string? VesselId { get; set; }
        }

        private static async Task SafePostAsync(CoordinationService coordination, string content)
        {
            try
            {
                await coordination.PostMessageAsync(
                    CoordinationService.DefaultRoomKey,
                    CoordinationAuthorTypeEnum.System,
                    null,
                    "armada",
                    content).ConfigureAwait(false);
            }
            catch
            {
                // The hold state change already happened; board mirroring is best-effort.
            }
        }

        /// <summary>
        /// Serializable rollup row for one campaign-tree objective.
        /// </summary>
        public sealed class CampaignNode
        {
            /// <summary>Objective identifier.</summary>
            public string Id { get; set; } = String.Empty;

            /// <summary>Title.</summary>
            public string Title { get; set; } = String.Empty;

            /// <summary>Lifecycle status.</summary>
            public ObjectiveStatusEnum? Status { get; set; }

            /// <summary>Backlog state.</summary>
            public ObjectiveBacklogStateEnum? BacklogState { get; set; }

            /// <summary>Kind.</summary>
            public ObjectiveKindEnum? Kind { get; set; }

            /// <summary>Priority.</summary>
            public ObjectivePriorityEnum? Priority { get; set; }

            /// <summary>Parent identifier.</summary>
            public string? ParentObjectiveId { get; set; }

            /// <summary>Tags.</summary>
            public List<string>? Tags { get; set; }

            /// <summary>
            /// Build a rollup row from an objective.
            /// </summary>
            public static CampaignNode From(Objective objective)
            {
                return new CampaignNode
                {
                    Id = objective.Id,
                    Title = objective.Title,
                    Status = objective.Status,
                    BacklogState = objective.BacklogState,
                    Kind = objective.Kind,
                    Priority = objective.Priority,
                    ParentObjectiveId = objective.ParentObjectiveId,
                    Tags = objective.Tags
                };
            }
        }

        private sealed class CampaignStatusArgs
        {
            public string? Tag { get; set; } = null;
            public string? RootObjectiveId { get; set; } = null;
            public int? NoteLimit { get; set; } = null;
        }

        private sealed class ClaimArgs
        {
            public string? Action { get; set; } = null;
            public string? SubjectType { get; set; } = null;
            public string? SubjectId { get; set; } = null;
            public string? Note { get; set; } = null;
            public string? ParticipantKey { get; set; } = null;
            public string? DisplayName { get; set; } = null;
            public double? TtlHours { get; set; } = null;
            public string? ClaimId { get; set; } = null;
            public string? RoomKey { get; set; } = null;
        }

        private sealed class DispatchHoldArgs
        {
            public string? Action { get; set; } = null;
            public string? Reason { get; set; } = null;
            public string? SetBy { get; set; } = null;
        }

        private sealed class CoordinationPostArgs
        {
            public string Content { get; set; } = "";
            public string AuthorName { get; set; } = "";
            public string? AuthorId { get; set; } = null;
            public string? RoomKey { get; set; } = null;
            public string? ObjectiveId { get; set; } = null;
            public string? VoyageId { get; set; } = null;
            public string? MissionId { get; set; } = null;
            public string? VesselId { get; set; } = null;
            public string? IncidentId { get; set; } = null;
            public string? ToParticipantKey { get; set; } = null;
        }

        /// <summary>Characters of a broadcast note kept when previewing.</summary>
        private const int _BOARD_PREVIEW_CHARS = 600;

        /// <summary>
        /// Project board notes for the read tool. A note addressed to one of
        /// <paramref name="callerAddresses"/> (the caller's key or a name it posts under) is never previewed, whatever
        /// <paramref name="includeFullContent"/> says; broadcast notes are previewed
        /// unless the caller asked for everything.
        /// </summary>
        /// <summary>
        /// Refusal for a caller that sends the participant header and also names a different
        /// participant key. The header is the session's identity: wakes are read and acknowledged
        /// as that key, so honouring a different key would show mail the session cannot
        /// acknowledge, and reading as the header key while the caller asked for another reads the
        /// wrong mailbox silently. Null when there is no header, no requested key, or they match.
        /// </summary>
        /// <param name="authenticatedKey">Participant key from the request header.</param>
        /// <param name="requestedKey">Participant key the caller passed.</param>
        /// <param name="baseKey">Configured key the bridge made unique for this session; the
        /// session knows only this key, so passing it means the header key.</param>
        /// <param name="headerKeyNames">Names the header key posted or heartbeated under within the
        /// address window; passing one of them means the header key.</param>
        /// <returns>The refusal text, or null.</returns>
        internal static string? ParticipantKeyRefusal(string? authenticatedKey, string? requestedKey, string? baseKey = null, IEnumerable<string>? headerKeyNames = null)
        {
            if (String.IsNullOrWhiteSpace(authenticatedKey) || String.IsNullOrWhiteSpace(requestedKey)) return null;
            if (String.Equals(authenticatedKey.Trim(), requestedKey.Trim(), StringComparison.Ordinal)) return null;
            if (!String.IsNullOrWhiteSpace(baseKey)
                && String.Equals(baseKey.Trim(), requestedKey.Trim(), StringComparison.Ordinal)) return null;
            if (headerKeyNames != null
                && headerKeyNames.Any(name => String.Equals(name, requestedKey.Trim(), StringComparison.OrdinalIgnoreCase))) return null;
            return "participantKey '" + requestedKey.Trim() + "' differs from this session's participant key '" + authenticatedKey.Trim()
                + "' (the " + ArmadaMcpHttpServer.ParticipantHeaderName + " header). The session reads and acknowledges wakes as '"
                + authenticatedKey.Trim() + "' only. Omit participantKey or pass '" + authenticatedKey.Trim()
                + "'; a note addressed to a name this session posts under is delivered to its participant key.";
        }

        /// <summary>
        /// Refusal for the current call's participant key, or null. A session behind a shared bridge
        /// configuration knows itself by the name it posts under, not by the key the bridge made for
        /// it, so a name the header key posted or heartbeated under within the address window means
        /// the header key. A name another key uses, and any other key, stays refused.
        /// </summary>
        private static async Task<string?> HeaderParticipantKeyRefusalAsync(CoordinationService? coordination, string? requestedKey)
        {
            string? headerKey = ArmadaMcpHttpServer.CurrentParticipantKey;
            string? baseKey = ArmadaMcpHttpServer.CurrentParticipantBaseKey;
            string? refusal = ParticipantKeyRefusal(headerKey, requestedKey, baseKey);
            if (refusal == null) return null;
            if (coordination == null) return refusal;

            List<string> names;
            try { names = await coordination.NamesDeliveredToAsync(headerKey!).ConfigureAwait(false); }
            catch (NotSupportedException) { return refusal; }
            return ParticipantKeyRefusal(headerKey, requestedKey, baseKey, names);
        }

        /// <summary>
        /// Every address whose notes are the caller's mail: its participant key and the names that key
        /// posted or heartbeated under within the address window. Empty for an anonymous read.
        /// </summary>
        private static async Task<HashSet<string>> CallerAddressesAsync(CoordinationService coordination, string? participantKey)
        {
            HashSet<string> addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrWhiteSpace(participantKey)) return addresses;
            addresses.Add(participantKey.Trim());
            try
            {
                foreach (string name in await coordination.NamesDeliveredToAsync(participantKey).ConfigureAwait(false))
                    addresses.Add(name);
            }
            catch (NotSupportedException)
            {
                // A backend without presence knows the key alone.
            }
            return addresses;
        }

        private static List<CoordinationMessageView> BuildMessageViews(
            List<CoordinationMessage> messages,
            HashSet<string> callerAddresses,
            bool includeFullContent)
        {
            List<CoordinationMessageView> views = new List<CoordinationMessageView>(messages.Count);
            foreach (CoordinationMessage message in messages)
            {
                string content = message.Content ?? String.Empty;
                bool addressedToCaller = !String.IsNullOrWhiteSpace(message.ToParticipantKey)
                    && callerAddresses.Contains(message.ToParticipantKey!.Trim());
                bool keepWhole = includeFullContent
                    || addressedToCaller
                    || content.Length <= _BOARD_PREVIEW_CHARS;

                views.Add(new CoordinationMessageView
                {
                    Id = message.Id,
                    AuthorType = message.AuthorType.ToString(),
                    AuthorName = message.AuthorName,
                    ToParticipantKey = message.ToParticipantKey,
                    Content = keepWhole ? content : content.Substring(0, _BOARD_PREVIEW_CHARS),
                    ContentLength = content.Length,
                    Truncated = !keepWhole,
                    VoyageId = message.VoyageId,
                    MissionId = message.MissionId,
                    VesselId = message.VesselId,
                    IncidentId = message.IncidentId,
                    CreatedUtc = message.CreatedUtc
                });
            }

            return views;
        }

        private sealed class CoordinationReadArgs
        {
            public string? RoomKey { get; set; } = null;
            public int? Limit { get; set; } = null;
            public DateTime? AfterUtc { get; set; } = null;
            public int? ActiveWithinMinutes { get; set; } = null;
            public string? ParticipantKey { get; set; } = null;
            public bool? IncludeFullContent { get; set; } = null;
            public string? MessageId { get; set; } = null;
        }

        /// <summary>
        /// One board note as the read tool returns it. Content is the whale: a full room
        /// read returned 57,501 characters and blew the caller's tool output limit, which
        /// cost every autonomous cycle several turns spilling the payload to a file and
        /// parsing it back.
        /// <para>
        /// Broadcast chatter is previewed. A note ADDRESSED to the caller is always returned
        /// whole, because directed mail is exactly what must not be lost; a wake names its
        /// note's id so the note can be read whole by id.
        /// </para>
        /// </summary>
        private sealed class CoordinationMessageView
        {
            /// <summary>Message identifier.</summary>
            public string Id { get; set; } = "";
            /// <summary>Author kind.</summary>
            public string AuthorType { get; set; } = "";
            /// <summary>Author display name.</summary>
            public string AuthorName { get; set; } = "";
            /// <summary>Participant this note is addressed to, or null for broadcast.</summary>
            public string? ToParticipantKey { get; set; }
            /// <summary>Note text, whole or previewed. See <see cref="Truncated"/>.</summary>
            public string Content { get; set; } = "";
            /// <summary>Length of the full note, whatever <see cref="Content"/> carries.</summary>
            public int ContentLength { get; set; }
            /// <summary>True when <see cref="Content"/> is a preview of a longer note.</summary>
            public bool Truncated { get; set; }
            /// <summary>Related voyage identifier, when present.</summary>
            public string? VoyageId { get; set; }
            /// <summary>Related mission identifier, when present.</summary>
            public string? MissionId { get; set; }
            /// <summary>Related vessel identifier, when present.</summary>
            public string? VesselId { get; set; }
            /// <summary>Related incident identifier, when present.</summary>
            public string? IncidentId { get; set; }
            /// <summary>Creation time.</summary>
            public DateTime CreatedUtc { get; set; }
            /// <summary>
            /// D11 attention label set only when the inbox_triage decision is in Gate mode and answered:
            /// informational, today, this_hour, or blocking_live_voyage. Null otherwise.
            /// </summary>
            public string? Attention { get; set; }
            /// <summary>
            /// D11 note kind set only when the inbox_triage decision is in Gate mode and answered:
            /// handoff, status, question, stop_sign, or hold_notice. Null otherwise.
            /// </summary>
            public string? NoteKind { get; set; }
        }

        /// <summary>
        /// Apply the D11 inbox_triage adapter to board-note views: annotate each note's
        /// <see cref="CoordinationMessageView.Attention"/> and <see cref="CoordinationMessageView.NoteKind"/>
        /// and re-order by attention in Gate mode. The adapter never hides a note, so with no adapter or
        /// the decision Off the input order is returned unchanged.
        /// </summary>
        /// <summary>
        /// The vessels a board note concerns, for the triage decision's egress vessel rule: its own vessel, its
        /// mission's vessel, and every vessel its voyage's missions run on.
        /// </summary>
        private static async Task<List<string>> BoardNoteVesselsAsync(CoordinationMessageView view, DatabaseDriver database)
        {
            List<string> vessels = new List<string>();
            if (!String.IsNullOrWhiteSpace(view.VesselId)) vessels.Add(view.VesselId!);
            if (!String.IsNullOrWhiteSpace(view.MissionId))
            {
                Mission? mission = await database.Missions.ReadAsync(view.MissionId!).ConfigureAwait(false);
                if (!String.IsNullOrWhiteSpace(mission?.VesselId)) vessels.Add(mission!.VesselId!);
            }
            if (!String.IsNullOrWhiteSpace(view.VoyageId))
            {
                foreach (Mission mission in await database.Missions.EnumerateByVoyageAsync(view.VoyageId!).ConfigureAwait(false))
                    if (!String.IsNullOrWhiteSpace(mission.VesselId)) vessels.Add(mission.VesselId!);
            }
            return vessels.Distinct(StringComparer.Ordinal).ToList();
        }

        private static async Task<List<CoordinationMessageView>> ApplyBoardNoteTriageAsync(
            List<CoordinationMessageView> views,
            Armada.Core.Services.InboxTriageAdapter? triageAdapter,
            DatabaseDriver database)
        {
            if (triageAdapter == null || views.Count == 0) return views;

            List<Armada.Core.Services.BoardNoteTriageInput> inputs = new List<Armada.Core.Services.BoardNoteTriageInput>(views.Count);
            try
            {
                foreach (CoordinationMessageView view in views)
                {
                    inputs.Add(new Armada.Core.Services.BoardNoteTriageInput
                    {
                        Id = view.Id,
                        AuthorType = view.AuthorType,
                        Content = view.Content,
                        MissionId = view.MissionId,
                        VesselIds = await BoardNoteVesselsAsync(view, database).ConfigureAwait(false)
                    });
                }
            }
            catch (Exception)
            {
                // A note's vessels that cannot be read cannot be cleared for egress either: nothing is sent and
                // the deterministic order stands.
                return views;
            }

            IReadOnlyList<Armada.Core.Services.BoardNoteTriage> triaged =
                await triageAdapter.TriageBoardNotesAsync(inputs, System.Threading.CancellationToken.None).ConfigureAwait(false);
            if (triaged.Count == 0) return views;

            Dictionary<string, Armada.Core.Services.BoardNoteTriage> byId =
                new Dictionary<string, Armada.Core.Services.BoardNoteTriage>(StringComparer.Ordinal);
            foreach (Armada.Core.Services.BoardNoteTriage entry in triaged) byId[entry.Id] = entry;

            foreach (CoordinationMessageView view in views)
            {
                if (byId.TryGetValue(view.Id, out Armada.Core.Services.BoardNoteTriage? entry) && entry != null)
                {
                    view.Attention = entry.Attention;
                    view.NoteKind = entry.NoteKind;
                }
            }

            // Re-order by attention rank; notes with no label keep their relative order after labelled
            // ones. Nothing is removed.
            return views
                .OrderByDescending(view => BoardNoteAttentionRank(view.Attention))
                .ThenByDescending(view => view.CreatedUtc)
                .ToList();
        }

        private static readonly string[] _AttentionOrder = { "informational", "today", "this_hour", "blocking_live_voyage" };

        private static int BoardNoteAttentionRank(string? attention)
        {
            if (String.IsNullOrEmpty(attention)) return -1;
            for (int i = 0; i < _AttentionOrder.Length; i++)
                if (String.Equals(_AttentionOrder[i], attention, StringComparison.Ordinal)) return i;
            return -1;
        }

        private sealed class CoordinationHeartbeatArgs
        {
            public string ParticipantKey { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string? RoomKey { get; set; } = null;
        }
    }
}
