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
        /// <param name="coordination">Coordination service.</param>
        /// <param name="dispatchHold">Optional dispatch hold shared with the admiral's dispatch paths.</param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, CoordinationService coordination, Armada.Core.Services.DispatchHold? dispatchHold = null)
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
                        incidentId = new { type = "string", description = "Optional related incident ID (inc_ prefix)." },
                        toParticipantKey = new { type = "string", description = "Optional participant key to address this note to - marks it as work or an answer directed at one session while staying visible to all." }
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
                "Read recent notes from the shared coordination board plus who is currently active. Read this before dispatching voyages or touching incidents so you do not duplicate another session's work. When called with participantKey and the response contains UnreadWakes, PAUSE what you are doing and address those messages first - they are directed work or answers waiting on you. Acknowledge each with armada_mark_signal_read. Long BROADCAST notes come back previewed, each carrying ContentLength and Truncated, with TruncatedMessageCount on the response; notes addressed to you and every UnreadWake are always whole. Pass includeFullContent=true, ideally with a smaller limit or an afterUtc, to read a previewed note in full.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        roomKey = new { type = "string", description = "Room key. Omit for the default fleet room." },
                        limit = new { type = "integer", description = "Maximum number of notes to return (default 50)." },
                        afterUtc = new { type = "string", description = "Optional ISO 8601 timestamp; returns only notes created after it." },
                        activeWithinMinutes = new { type = "integer", description = "Presence window in minutes (default 15)." },
                        participantKey = new { type = "string", description = "Your session participant key. When supplied, notes are filtered to broadcast plus notes addressed to you - how a joining session picks up work handed to it." },
                        includeFullContent = new { type = "boolean", description = "Return every note whole instead of previewing long broadcast notes (default false). Notes addressed to you and UnreadWakes are always whole either way." }
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
                        List<CoordinationMessage> messages = await coordination.ReadMessagesAsync(
                            roomKey, request.AfterUtc, limit,
                            token: System.Threading.CancellationToken.None,
                            visibleToParticipantKey: String.IsNullOrWhiteSpace(request.ParticipantKey) ? null : request.ParticipantKey).ConfigureAwait(false);

                        List<Signal> unreadWakes = new List<Signal>();
                        if (!String.IsNullOrWhiteSpace(request.ParticipantKey))
                        {
                            try { unreadWakes = await coordination.EnumerateUnreadWakesAsync(request.ParticipantKey!).ConfigureAwait(false); }
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
                            messages, request.ParticipantKey, request.IncludeFullContent == true);
                        int truncatedCount = views.Count(view => view.Truncated);

                        return (object)new
                        {
                            RoomKey = roomKey,
                            Messages = views,
                            TruncatedMessageCount = truncatedCount,
                            ActiveParticipants = participants,
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
                "Send a presence heartbeat to the shared coordination board. Call periodically while working so other sessions can see you are active, and to prune stale presence. The response carries UnreadWakes: when non-empty, PAUSE your current work and address those directed messages first, then acknowledge each with armada_mark_signal_read.",
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

                        List<Signal> unreadWakes = new List<Signal>();
                        try { unreadWakes = await coordination.EnumerateUnreadWakesAsync(request.ParticipantKey!).ConfigureAwait(false); }
                        catch (NotSupportedException) { }

                        return (object)new { Participant = participant, UnreadWakes = unreadWakes };
                    }
                    catch (NotSupportedException ex)
                    {
                        return (object)new { Error = ex.Message };
                    }
                });

            register(
                "armada_coordination_claim",
                "Reserve work so other operator sessions do not dispatch the same thing. Claims are visible to everyone, expire after a few hours unless your heartbeats keep them alive, and dispatches against a claimed vessel or objective announce the overlap on the board. Actions: claim (subjectType + subjectId required), release (claimId), list (optional subjectType + subjectId).",
                new
                {
                    type = "object",
                    properties = new
                    {
                        action = new { type = "string", description = "claim | release | list" },
                        subjectType = new { type = "string", description = "vessel | objective. Required for claim." },
                        subjectId = new { type = "string", description = "The vsl_ or obj_ identifier to reserve. Required for claim." },
                        note = new { type = "string", description = "What you intend to do with the subject." },
                        participantKey = new { type = "string", description = "Stable key for your session. Required for claim." },
                        displayName = new { type="string", description = "Display name for the board. Required for claim." },
                        ttlHours = new { type = "number", description = "Hours until expiry without heartbeat. Default 4, clamped 0.5-72." },
                        claimId = new { type = "string", description = "Claim ID (ccl_ prefix). Required for release." },
                        roomKey = new { type = "string", description = "Room key. Omit for the default fleet room." }
                    },
                    required = new[] { "action" }
                },
                async (args) =>
                {
                    ClaimArgs request = JsonSerializer.Deserialize<ClaimArgs>(args!.Value, _JsonOptions)!;
                    string action = String.IsNullOrWhiteSpace(request.Action) ? "list" : request.Action!.Trim().ToLowerInvariant();

                    try
                    {
                        if (String.Equals(action, "claim", StringComparison.Ordinal))
                        {
                            if (String.IsNullOrWhiteSpace(request.SubjectId) || String.IsNullOrWhiteSpace(request.SubjectType))
                                return (object)new { Error = "subjectType and subjectId are required for action=claim" };
                            if (String.IsNullOrWhiteSpace(request.ParticipantKey) || String.IsNullOrWhiteSpace(request.DisplayName))
                                return (object)new { Error = "participantKey and displayName are required for action=claim - name your session" };

                            CoordinationClaimSubjectEnum subjectType;
                            if (!Enum.TryParse(request.SubjectType!, true, out subjectType))
                                return (object)new { Error = "subjectType must be vessel or objective" };

                            CoordinationClaim claim = await coordination.ClaimAsync(
                                request.ParticipantKey!, request.DisplayName!, subjectType, request.SubjectId!,
                                request.Note, request.TtlHours ?? 4, request.RoomKey).ConfigureAwait(false);
                            return (object)claim;
                        }

                        if (String.Equals(action, "release", StringComparison.Ordinal))
                        {
                            if (String.IsNullOrWhiteSpace(request.ClaimId))
                                return (object)new { Error = "claimId is required for action=release" };
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
                "One-call status for a campaign: the objective tree under a root (or every root carrying a tag), grouped by parent with statuses, plus active coordination claims and recent board notes. Use this to start a session or answer 'where does this campaign stand' without a pile of enumerations.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        tag = new { type = "string", description = "Campaign tag, for example 'campaign:porting'. Resolves every objective carrying it as a root. Omit when rootObjectiveId is supplied." },
                        rootObjectiveId = new { type = "string", description = "Campaign root objective ID (obj_ prefix). Omit when tag is supplied." },
                        noteLimit = new { type = "integer", description = "Recent board notes to include (default 10)." }
                    }
                },
                async (args) =>
                {
                    CampaignStatusArgs request = args != null && args.HasValue
                        ? JsonSerializer.Deserialize<CampaignStatusArgs>(args.Value, _JsonOptions) ?? new CampaignStatusArgs()
                        : new CampaignStatusArgs();

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
                                lanes.Add(new { objective = lane, children = slices ?? new List<CampaignNode>() });
                            }
                        }

                        tree.Add(new { objective = root, children = lanes });
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

                    return (object)new { Roots = rootIds, Tree = tree, ActiveClaims = claims, RecentNotes = notes };
                });

            if (dispatchHold != null)
            {
                register(
                    "armada_dispatch_hold",
                    "Engage, clear, or inspect the fleet-wide dispatch hold. Engage it BEFORE rebuilding or redeploying the admiral so other sessions and the objective scheduler cannot start new voyages against a binary about to change. In-flight voyages continue; only new dispatches are refused. Engaging or clearing posts a system note to the coordination board.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            action = new { type = "string", description = "engage | clear | status" },
                            reason = new { type = "string", description = "Why the hold is engaged. Required for engage." },
                            setBy = new { type = "string", description = "Your session or operator name. Required for engage." }
                        },
                        required = new[] { "action" }
                    },
                    async (args) =>
                    {
                        DispatchHoldArgs request = JsonSerializer.Deserialize<DispatchHoldArgs>(args!.Value, _JsonOptions)!;
                        string action = String.IsNullOrWhiteSpace(request.Action) ? "status" : request.Action!.Trim().ToLowerInvariant();

                        if (String.Equals(action, "status", StringComparison.Ordinal))
                            return (object)new { Active = dispatchHold.Snapshot() != null, Hold = dispatchHold.Snapshot() };

                        if (String.Equals(action, "clear", StringComparison.Ordinal))
                        {
                            bool wasActive = dispatchHold.Snapshot() != null;
                            dispatchHold.Clear();
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
                            if (String.IsNullOrWhiteSpace(request.SetBy))
                                return (object)new { Error = "setBy is required when engaging the hold - name your session so others know who to ask" };

                            dispatchHold.Engage(request.Reason!, request.SetBy);
                            await SafePostAsync(coordination, "[hold] Dispatching paused by " + request.SetBy + ": " + request.Reason + " Hold off dispatching new voyages until this is cleared.").ConfigureAwait(false);
                            await coordination.EmitHoldWakeAsync("[hold] Dispatching paused by " + request.SetBy + " (" + request.Reason + "). Hold new voyages; you will be woken on clear.").ConfigureAwait(false);
                            return (object)new { Active = true, Hold = dispatchHold.Snapshot() };
                        }

                        return (object)new { Error = "action must be engage, clear, or status" };
                    });
            }
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
            public string? VoyageId { get; set; } = null;
            public string? MissionId { get; set; } = null;
            public string? VesselId { get; set; } = null;
            public string? IncidentId { get; set; } = null;
            public string? ToParticipantKey { get; set; } = null;
        }

        /// <summary>Characters of a broadcast note kept when previewing.</summary>
        private const int _BOARD_PREVIEW_CHARS = 600;

        /// <summary>
        /// Project board notes for the read tool. A note addressed to
        /// <paramref name="participantKey"/> is never previewed, whatever
        /// <paramref name="includeFullContent"/> says; broadcast notes are previewed
        /// unless the caller asked for everything.
        /// </summary>
        private static List<CoordinationMessageView> BuildMessageViews(
            List<CoordinationMessage> messages,
            string? participantKey,
            bool includeFullContent)
        {
            List<CoordinationMessageView> views = new List<CoordinationMessageView>(messages.Count);
            foreach (CoordinationMessage message in messages)
            {
                string content = message.Content ?? String.Empty;
                bool addressedToCaller = !String.IsNullOrWhiteSpace(participantKey)
                    && String.Equals(message.ToParticipantKey, participantKey, StringComparison.Ordinal);
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
        }

        /// <summary>
        /// One board note as the read tool returns it. Content is the whale: a full room
        /// read returned 57,501 characters and blew the caller's tool output limit, which
        /// cost every autonomous cycle several turns spilling the payload to a file and
        /// parsing it back.
        /// <para>
        /// Broadcast chatter is previewed. A note ADDRESSED to the caller, and every
        /// unread wake, is always returned whole -- a truncated board preview has already
        /// hidden five complete reports once, and directed mail is exactly what must not
        /// be lost.
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
        }

        private sealed class CoordinationHeartbeatArgs
        {
            public string ParticipantKey { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string? RoomKey { get; set; } = null;
        }
    }
}
