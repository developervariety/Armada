namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// MCP tools exposing the auto-land safety-net audit queue and verdict recording.
    /// armada_list_judge_followups: orchestrator lists the follow-ups Judge reviews suggested.
    /// armada_record_judge_followup_handled: orchestrator records that one follow-up item has a home.
    /// armada_drain_audit_queue: orchestrator pulls Pending entries flagged for deep review.
    /// armada_record_audit_verdict: orchestrator records the subagent's Pass/Concern/Critical verdict.
    /// </summary>
    public static class McpAuditTools
    {
        /// <summary>Registered name of the Judge follow-up listing tool.</summary>
        public const string ListJudgeFollowUpsToolName = "armada_list_judge_followups";

        /// <summary>Registered name of the tool that records a handled marker for one follow-up item.</summary>
        public const string RecordJudgeFollowUpHandledToolName = "armada_record_judge_followup_handled";

        /// <summary>Refusal reason: the caller holds a mission credential.</summary>
        public const string HandledOperatorRequiredReason = "operator_required";

        /// <summary>Refusal reason: no Judge follow-up has the given id.</summary>
        public const string HandledFollowUpNotFoundReason = "follow_up_not_found";

        /// <summary>Refusal reason: the item number is not one of the follow-up's items.</summary>
        public const string HandledItemOutOfRangeReason = "item_out_of_range";

        /// <summary>Refusal reason: the kind is not Objective, Decision or Note.</summary>
        public const string HandledKindInvalidReason = "kind_invalid";

        /// <summary>Refusal reason: the disposition is blank.</summary>
        public const string HandledDispositionRequiredReason = "disposition_required";

        /// <summary>Refusal reason: an Objective disposition names no stored objective.</summary>
        public const string HandledObjectiveNotFoundReason = "objective_not_found";

        /// <summary>Refusal reason: the item already holds a marker and replace was not requested.</summary>
        public const string HandledItemAlreadyHandledReason = "item_already_handled";

        private static readonly JsonSerializerOptions _ListJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private sealed class ListJudgeFollowUpsArgs
        {
            public string? VesselId { get; set; }

            public int? SinceHours { get; set; }

            public int? Limit { get; set; }

            public int? Offset { get; set; }

            public bool? IncludeHandled { get; set; }
        }

        private sealed class RecordJudgeFollowUpHandledArgs
        {
            public string? FollowUpId { get; set; }

            public int? ItemNumber { get; set; }

            public string? Kind { get; set; }

            public string? Disposition { get; set; }

            public string? Operator { get; set; }

            public bool? Replace { get; set; }
        }

        private sealed class JudgeFollowUpItemView
        {
            public int Number { get; set; }

            public string Text { get; set; } = "";

            public JudgeFollowUpHandledItem? Handled { get; set; }
        }

        private sealed class JudgeFollowUpListItem
        {
            public string Id { get; set; } = "";

            public string JudgeMissionId { get; set; } = "";

            public string ReviewedMissionId { get; set; } = "";

            public string? VoyageId { get; set; }

            public string? VesselId { get; set; }

            public string JudgeVerdict { get; set; } = "";

            public DateTime CreatedUtc { get; set; }

            public List<JudgeFollowUpItemView> Items { get; set; } = new List<JudgeFollowUpItemView>();
        }

        private class AuditQueueItem
        {
            public DateTime CreatedUtc { get; set; }
            public MergeEntry? MergeEntry { get; set; }
            public JudgeFollowUp? JudgeFollowUp { get; set; }
        }

        private static string MarkerKey(string followUpId, int itemNumber)
        {
            return followUpId + "#" + itemNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static object Refusal(string reason, string message)
        {
            return new { Error = message, Reason = reason };
        }

        /// <summary>
        /// Registers audit MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for data access.</param>
        /// <param name="remoteTriggerService">Optional remote trigger service; when provided, fires FireCriticalAsync on Critical verdicts.</param>
        public static void Register(
            RegisterToolDelegate register,
            DatabaseDriver database,
            IRemoteTriggerService? remoteTriggerService = null,
            Armada.Core.Services.FollowUpRoutingAdapter? followUpRoutingAdapter = null)
        {
            JudgeFollowUpService followUpService = new JudgeFollowUpService(database, new SyslogLogging.LoggingModule())
            {
                // D12 followup_routing: when the operator backfills or captures follow-ups through this
                // tool, route each item to a home in Gate mode. Null or Off leaves capture unchanged.
                RoutingAdapter = followUpRoutingAdapter
            };
            JudgeFollowUpBackfillService backfillService = new JudgeFollowUpBackfillService(database, new SyslogLogging.LoggingModule())
            {
                RoutingAdapter = followUpRoutingAdapter
            };
            register(
                "armada_backfill_judge_followups",
                "Backfills missed durable Judge follow-up captures over a bounded UTC time range; safe to repeat",
                new
                {
                    type = "object",
                    properties = new
                    {
                        fromUtc = new { type = "string", description = "Exclusive UTC range start in ISO-8601 format" },
                        toUtc = new { type = "string", description = "Exclusive UTC range end in ISO-8601 format; defaults to now" },
                        dryRun = new { type = "boolean", description = "Report missing rows without writing them" },
                        maxPages = new { type = "integer", description = "Maximum 500-row pages to scan (default 100, max 1000)" }
                    },
                    required = new[] { "fromUtc" }
                },
                async (args) =>
                {
                    if (!args.HasValue
                        || !args.Value.TryGetProperty("fromUtc", out JsonElement fromElement)
                        || fromElement.ValueKind != JsonValueKind.String
                        || !DateTime.TryParse(fromElement.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime fromUtc))
                        return (object)new { Error = "fromUtc must be an ISO-8601 timestamp" };

                    DateTime toUtc = DateTime.UtcNow;
                    if (args.Value.TryGetProperty("toUtc", out JsonElement toElement)
                        && (toElement.ValueKind != JsonValueKind.String
                            || !DateTime.TryParse(toElement.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out toUtc)))
                        return (object)new { Error = "toUtc must be an ISO-8601 timestamp" };
                    bool dryRun = args.Value.TryGetProperty("dryRun", out JsonElement dryRunElement)
                        && dryRunElement.ValueKind == JsonValueKind.True;
                    int maxPages = 100;
                    if (args.Value.TryGetProperty("maxPages", out JsonElement maxPagesElement)
                        && maxPagesElement.ValueKind == JsonValueKind.Number)
                        maxPages = Math.Clamp(maxPagesElement.GetInt32(), 1, 1000);
                    if (toUtc <= fromUtc) return (object)new { Error = "toUtc must be later than fromUtc" };

                    return await backfillService.RunAsync(
                        fromUtc.ToUniversalTime(),
                        toUtc.ToUniversalTime(),
                        dryRun,
                        maxPages).ConfigureAwait(false);
                });
            register(
                ListJudgeFollowUpsToolName,
                "List the follow-ups Judge reviews suggested, newest first: each stored Judge review with at least one open suggested follow-up, split into numbered items. Items an operator marked handled with " + RecordJudgeFollowUpHandledToolName + " are left out and counted in HandledItemsOmitted unless includeHandled is true. Use it to sweep follow-ups the same way as papercuts, so each item gets a home (a fix objective, an owner decision, or a recorded note). Read-only.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Optional: filter to one vessel (vsl_ prefix)" },
                        sinceHours = new { type = "integer", description = "Only reviews newer than this many hours (default 168, maximum 2160)" },
                        limit = new { type = "integer", description = "Maximum reviews returned (default 25, maximum 200)" },
                        offset = new { type = "integer", description = "Reviews to skip, for the next page (default 0)" },
                        includeHandled = new { type = "boolean", description = "Also list items already marked handled, each with its marker (default false)" }
                    }
                },
                async (args) =>
                {
                    ListJudgeFollowUpsArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<ListJudgeFollowUpsArgs>(args.Value, _ListJsonOptions) ?? new ListJudgeFollowUpsArgs()
                        : new ListJudgeFollowUpsArgs();
                    int sinceHours = Math.Clamp(request.SinceHours ?? 168, 1, 2160);
                    int limit = Math.Clamp(request.Limit ?? 25, 1, 200);
                    int offset = Math.Max(0, request.Offset ?? 0);
                    DateTime sinceUtc = DateTime.UtcNow.AddHours(-sinceHours);
                    string? vesselId = String.IsNullOrWhiteSpace(request.VesselId) ? null : request.VesselId!.Trim();

                    bool includeHandled = request.IncludeHandled ?? false;

                    List<JudgeFollowUp> rows = await database.JudgeFollowUps.EnumerateCreatedSinceAsync(sinceUtc, vesselId).ConfigureAwait(false);
                    Dictionary<string, JudgeFollowUpHandledItem> markers = new Dictionary<string, JudgeFollowUpHandledItem>(StringComparer.Ordinal);
                    foreach (JudgeFollowUpHandledItem marker in await database.JudgeFollowUpHandledItems
                        .EnumerateForFollowUpsAsync(rows.Select(row => row.Id).ToList()).ConfigureAwait(false))
                        markers[MarkerKey(marker.FollowUpId, marker.ItemNumber)] = marker;

                    List<JudgeFollowUpListItem> withItems = new List<JudgeFollowUpListItem>();
                    int totalItems = 0;
                    int openItems = 0;
                    int handledOmitted = 0;
                    foreach (JudgeFollowUp row in rows)
                    {
                        List<string> texts = JudgeFollowUpItems.Split(row.SuggestedFollowUps);
                        if (texts.Count == 0) continue;
                        totalItems += texts.Count;
                        List<JudgeFollowUpItemView> items = new List<JudgeFollowUpItemView>();
                        for (int index = 0; index < texts.Count; index++)
                        {
                            int number = index + 1;
                            markers.TryGetValue(MarkerKey(row.Id, number), out JudgeFollowUpHandledItem? handled);
                            if (handled == null) openItems++;
                            else if (!includeHandled)
                            {
                                handledOmitted++;
                                continue;
                            }
                            items.Add(new JudgeFollowUpItemView { Number = number, Text = texts[index], Handled = handled });
                        }
                        if (items.Count == 0) continue;
                        withItems.Add(new JudgeFollowUpListItem
                        {
                            Id = row.Id,
                            JudgeMissionId = row.JudgeMissionId,
                            ReviewedMissionId = row.ReviewedMissionId,
                            VoyageId = row.VoyageId,
                            VesselId = row.VesselId,
                            JudgeVerdict = row.JudgeVerdict,
                            CreatedUtc = row.CreatedUtc,
                            Items = items
                        });
                    }

                    List<JudgeFollowUpListItem> page = withItems.Skip(offset).Take(limit).ToList();
                    return (object)new
                    {
                        SinceUtc = sinceUtc,
                        ReviewsScanned = rows.Count,
                        ReviewsWithFollowUps = withItems.Count,
                        TotalItems = totalItems,
                        OpenItems = openItems,
                        HandledItemsOmitted = handledOmitted,
                        Offset = offset,
                        Count = page.Count,
                        HasMore = offset + page.Count < withItems.Count,
                        FollowUps = page
                    };
                });
            register(
                RecordJudgeFollowUpHandledToolName,
                "Record that one Judge follow-up item has a home, so " + ListJudgeFollowUpsToolName + " stops listing it. Address the item by its follow-up id and its item number from the listing. kind is Objective (disposition = the fix objective id), Decision (disposition = the owner decision) or Note (disposition = the note). An item holds one marker; pass replace=true to correct it. Operator only.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        followUpId = new { type = "string", description = "Judge follow-up id (jfu_ prefix)" },
                        itemNumber = new { type = "integer", description = "One-based item number, as listed" },
                        kind = new { type = "string", description = "Objective | Decision | Note" },
                        disposition = new { type = "string", description = "The objective id, the decision, or the note" },
                        @operator = new { type = "string", description = "Who handled the item; defaults to the caller" },
                        replace = new { type = "boolean", description = "Replace an existing marker for this item (default false)" }
                    },
                    required = new[] { "followUpId", "itemNumber", "kind", "disposition" }
                },
                async (args) =>
                {
                    AuthContext caller = Armada.Server.Mcp.McpCallerContext.Require();
                    if (!String.IsNullOrEmpty(caller.MissionId))
                        return Refusal(HandledOperatorRequiredReason, "only an operator can record a handled marker");

                    RecordJudgeFollowUpHandledArgs request = args.HasValue
                        ? JsonSerializer.Deserialize<RecordJudgeFollowUpHandledArgs>(args.Value, _ListJsonOptions) ?? new RecordJudgeFollowUpHandledArgs()
                        : new RecordJudgeFollowUpHandledArgs();
                    string followUpId = request.FollowUpId?.Trim() ?? String.Empty;
                    if (followUpId.Length == 0) return Refusal(HandledFollowUpNotFoundReason, "followUpId is required");
                    JudgeFollowUp? followUp = await database.JudgeFollowUps.ReadAsync(followUpId).ConfigureAwait(false);
                    if (followUp == null) return Refusal(HandledFollowUpNotFoundReason, "judge follow-up not found: " + followUpId);

                    int itemCount = JudgeFollowUpItems.Split(followUp.SuggestedFollowUps).Count;
                    int itemNumber = request.ItemNumber ?? 0;
                    if (itemNumber < 1 || itemNumber > itemCount)
                        return Refusal(HandledItemOutOfRangeReason, "item " + itemNumber + " is out of range: follow-up " + followUp.Id + " has " + itemCount + " item(s), numbered from 1");

                    if (String.IsNullOrWhiteSpace(request.Kind) || !Enum.TryParse(request.Kind.Trim(), true, out JudgeFollowUpDispositionKindEnum kind) || !Enum.IsDefined(kind))
                        return Refusal(HandledKindInvalidReason, "kind must be Objective, Decision or Note");
                    string disposition = request.Disposition?.Trim() ?? String.Empty;
                    if (disposition.Length == 0) return Refusal(HandledDispositionRequiredReason, "disposition is required");
                    if (kind == JudgeFollowUpDispositionKindEnum.Objective
                        && await database.Objectives.ReadAsync(disposition).ConfigureAwait(false) == null)
                        return Refusal(HandledObjectiveNotFoundReason, "objective not found: " + disposition);

                    string handledBy = !String.IsNullOrWhiteSpace(request.Operator)
                        ? request.Operator.Trim()
                        : caller.PrincipalDisplay ?? caller.UserId ?? "operator";
                    JudgeFollowUpHandledItem marker = new JudgeFollowUpHandledItem
                    {
                        TenantId = followUp.TenantId,
                        UserId = followUp.UserId,
                        FollowUpId = followUp.Id,
                        ItemNumber = itemNumber,
                        Kind = kind,
                        Disposition = disposition,
                        HandledBy = handledBy,
                        HandledUtc = DateTime.UtcNow
                    };

                    if (request.Replace ?? false)
                    {
                        await database.JudgeFollowUpHandledItems.ReplaceAsync(marker).ConfigureAwait(false);
                    }
                    else if (!await database.JudgeFollowUpHandledItems.CreateAsync(marker).ConfigureAwait(false))
                    {
                        JudgeFollowUpHandledItem? existing = await database.JudgeFollowUpHandledItems.ReadAsync(followUp.Id, itemNumber).ConfigureAwait(false);
                        return (object)new
                        {
                            Error = "item " + itemNumber + " of follow-up " + followUp.Id + " is already handled; pass replace=true to correct it",
                            Reason = HandledItemAlreadyHandledReason,
                            Existing = existing
                        };
                    }

                    JudgeFollowUpHandledItem? stored = await database.JudgeFollowUpHandledItems.ReadAsync(followUp.Id, itemNumber).ConfigureAwait(false);
                    return (object)(stored ?? marker);
                });
            register(
                "armada_drain_audit_queue",
                "Returns Pending deep-review merge entries oldest-first for orchestrator audit processing",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Optional: filter to one vessel" },
                        limit = new { type = "integer", description = "Max entries to return (default 10, max 50)" }
                    }
                },
                async (args) =>
                {
                    string? vesselId = null;
                    int limit = 10;
                    if (args.HasValue && args.Value.TryGetProperty("vesselId", out JsonElement vidEl) && vidEl.ValueKind == JsonValueKind.String)
                        vesselId = vidEl.GetString();
                    if (args.HasValue && args.Value.TryGetProperty("limit", out JsonElement l) && l.ValueKind == JsonValueKind.Number)
                        limit = Math.Clamp(l.GetInt32(), 1, 50);

                    await followUpService.ReconcilePendingAssociationsAsync(vesselId).ConfigureAwait(false);

                    List<MergeEntry> all = await database.MergeEntries.EnumerateAsync().ConfigureAwait(false);
                    List<JudgeFollowUp> pendingFollowUps = await database.JudgeFollowUps
                        .EnumeratePendingAsync(vesselId)
                        .ConfigureAwait(false);
                    HashSet<string> linkedEntryIds = pendingFollowUps
                        .Where(followUp => !String.IsNullOrWhiteSpace(followUp.MergeEntryId))
                        .Select(followUp => followUp.MergeEntryId!)
                        .ToHashSet(StringComparer.Ordinal);
                    IEnumerable<MergeEntry> pendingEntries = all
                        .Where(e => e.AuditDeepPicked == true
                                 && e.AuditDeepVerdict == "Pending"
                                 && e.AuditDeepCompletedUtc == null
                                 && (vesselId == null || e.VesselId == vesselId));

                    List<AuditQueueItem> pending = new List<AuditQueueItem>();
                    foreach (MergeEntry entry in pendingEntries)
                    {
                        if (linkedEntryIds.Contains(entry.Id)) continue;
                        JudgeFollowUp? canonical = await database.JudgeFollowUps
                            .ReadByMergeEntryAsync(entry.Id)
                            .ConfigureAwait(false);
                        if (canonical != null) continue;
                        pending.Add(new AuditQueueItem
                        {
                            CreatedUtc = entry.CreatedUtc,
                            MergeEntry = entry
                        });
                    }
                    pending.AddRange(pendingFollowUps.Select(followUp => new AuditQueueItem
                    {
                        CreatedUtc = followUp.CreatedUtc,
                        JudgeFollowUp = followUp
                    }));

                    List<object> results = new List<object>();
                    foreach (AuditQueueItem item in pending
                        .OrderBy(candidate => candidate.CreatedUtc)
                        .ThenBy(candidate => candidate.MergeEntry?.Id ?? candidate.JudgeFollowUp!.Id, StringComparer.Ordinal)
                        .Take(limit))
                    {
                        if (item.JudgeFollowUp != null)
                        {
                            JudgeFollowUp followUp = item.JudgeFollowUp;
                            MergeEntry? linkedEntry = String.IsNullOrWhiteSpace(followUp.MergeEntryId)
                                ? null
                                : all.FirstOrDefault(entry => String.Equals(entry.Id, followUp.MergeEntryId, StringComparison.Ordinal));
                            Vessel? followUpVessel = String.IsNullOrWhiteSpace(followUp.VesselId)
                                ? null
                                : await database.Vessels.ReadAsync(followUp.VesselId).ConfigureAwait(false);
                            bool followUpCalibration = (followUpVessel?.AutoLandCalibrationLandedCount ?? 0) < 50;
                            results.Add(new
                            {
                                kind = "judgeFollowUp",
                                auditItemId = followUp.Id,
                                followUpId = followUp.Id,
                                judgeMissionId = followUp.JudgeMissionId,
                                missionId = followUp.ReviewedMissionId,
                                entryId = followUp.MergeEntryId,
                                vesselId = followUp.VesselId,
                                judgeVerdict = followUp.JudgeVerdict,
                                suggestedFollowUps = followUp.SuggestedFollowUps,
                                branchName = linkedEntry?.BranchName,
                                auditLane = linkedEntry?.AuditLane,
                                auditCriticalTrigger = linkedEntry?.AuditCriticalTrigger,
                                auditConventionNotes = linkedEntry?.AuditConventionNotes,
                                isCalibration = followUpCalibration
                            });
                            continue;
                        }

                        MergeEntry entry = item.MergeEntry!;
                        Vessel? vessel = await database.Vessels.ReadAsync(entry.VesselId!).ConfigureAwait(false);
                        bool isCalibration = (vessel?.AutoLandCalibrationLandedCount ?? 0) < 50;
                        results.Add(new
                        {
                            kind = "mergeEntry",
                            auditItemId = entry.Id,
                            entryId = entry.Id,
                            missionId = entry.MissionId,
                            vesselId = entry.VesselId,
                            branchName = entry.BranchName,
                            auditLane = entry.AuditLane,
                            auditCriticalTrigger = entry.AuditCriticalTrigger,
                            auditConventionNotes = entry.AuditConventionNotes,
                            isCalibration
                        });
                    }

                    return (object)new { entries = results };
                });

            register(
                "armada_record_audit_verdict",
                "Records the orchestrator-provided audit verdict (Pass/Concern/Critical) on a deep-review merge entry",
                new
                {
                    type = "object",
                    properties = new
                    {
                        entryId = new { type = "string", description = "Merge entry ID (mrg_ prefix); mutually exclusive with followUpId" },
                        followUpId = new { type = "string", description = "Judge follow-up ID (jfu_ prefix); mutually exclusive with entryId" },
                        verdict = new { type = "string", description = "Pass | Concern | Critical" },
                        notes = new { type = "string", description = "Subagent audit notes" },
                        recommendedAction = new { type = "string", description = "Required when verdict = Critical; null otherwise" }
                    },
                    required = new[] { "verdict", "notes" }
                },
                async (args) =>
                {
                    if (!args.HasValue) return (object)new { Error = "missing args" };

                    string? entryId = args.Value.TryGetProperty("entryId", out JsonElement entryIdElement)
                        && entryIdElement.ValueKind == JsonValueKind.String
                        ? entryIdElement.GetString()
                        : null;
                    string? followUpId = args.Value.TryGetProperty("followUpId", out JsonElement followUpIdElement)
                        && followUpIdElement.ValueKind == JsonValueKind.String
                        ? followUpIdElement.GetString()
                        : null;
                    string verdict = args.Value.GetProperty("verdict").GetString()!;
                    string notes = args.Value.GetProperty("notes").GetString()!;
                    string? recAction = null;
                    if (args.Value.TryGetProperty("recommendedAction", out JsonElement ra) && ra.ValueKind == JsonValueKind.String)
                        recAction = ra.GetString();

                    if (verdict != "Pass" && verdict != "Concern" && verdict != "Critical")
                        return (object)new { Error = "verdict must be Pass | Concern | Critical" };
                    if (verdict == "Critical" && string.IsNullOrEmpty(recAction))
                        return (object)new { Error = "recommendedAction required when verdict = Critical" };
                    if (String.IsNullOrWhiteSpace(entryId) == String.IsNullOrWhiteSpace(followUpId))
                        return (object)new { Error = "provide exactly one of entryId or followUpId" };

                    if (String.IsNullOrWhiteSpace(followUpId) && !String.IsNullOrWhiteSpace(entryId))
                    {
                        List<JudgeFollowUp> linkedPending = await database.JudgeFollowUps
                            .EnumeratePendingAsync()
                            .ConfigureAwait(false);
                        followUpId = linkedPending
                            .FirstOrDefault(item => String.Equals(item.MergeEntryId, entryId, StringComparison.Ordinal))
                            ?.Id;
                    }

                    if (!String.IsNullOrWhiteSpace(followUpId))
                    {
                        JudgeFollowUp? followUp = await database.JudgeFollowUps.ReadAsync(followUpId).ConfigureAwait(false);
                        if (followUp == null) return (object)new { Error = "judge follow-up not found: " + followUpId };

                        DateTime completedUtc = DateTime.UtcNow;
                        followUp = await followUpService.CompleteAuditAsync(
                            followUp.Id,
                            verdict,
                            notes,
                            recAction,
                            completedUtc).ConfigureAwait(false);

                        if (verdict == "Critical" && remoteTriggerService != null)
                        {
                            string followUpContext = "audit Critical on Judge follow-up " + followUp.Id + " :: "
                                + notes + " :: ACTION: " + (recAction ?? "(none)");
                            try
                            {
                                await remoteTriggerService.FireCriticalAsync(followUpContext).ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                // Fire failure does not affect verdict recording.
                            }
                        }

                        return (object)followUp;
                    }

                    MergeEntry? entry = await database.MergeEntries.ReadAsync(entryId!).ConfigureAwait(false);
                    if (entry == null) return (object)new { Error = "merge entry not found: " + entryId };

                    entry.AuditDeepVerdict = verdict;
                    entry.AuditDeepNotes = notes;
                    entry.AuditDeepRecommendedAction = recAction;
                    entry.AuditDeepCompletedUtc = DateTime.UtcNow;
                    entry.LastUpdateUtc = DateTime.UtcNow;
                    entry = await database.MergeEntries.UpdateAsync(entry).ConfigureAwait(false);

                    if (verdict == "Critical" && remoteTriggerService != null)
                    {
                        string ctx = "audit Critical on entry " + entry.Id + " :: " + (notes ?? "") + " :: ACTION: " + (recAction ?? "(none)");
                        try
                        {
                            await remoteTriggerService.FireCriticalAsync(ctx).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // fire failure does not affect verdict recording
                        }
                    }

                    return (object)entry;
                });
        }
    }
}
