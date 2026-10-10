namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services;

    /// <summary>
    /// Registers MCP tools for reading captain-reported papercuts.
    /// </summary>
    public static class McpPapercutTools
    {
        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private const int _DefaultLimit = 25;
        private const int _MaxLimit = 200;
        private const int _DefaultScanLimit = 500;
        private const int _MaxScanLimit = 5000;
        private static readonly TimeSpan _ListCallTimeout = TimeSpan.FromSeconds(120);

        /// <summary>
        /// Registers papercut MCP tools with the server.
        /// </summary>
        /// <param name="register">Delegate to register each tool.</param>
        /// <param name="database">Database driver for event data access.</param>
        /// <param name="mergeAdapter">
        /// The D6 <c>papercut_merge</c> adapter, or null to list without model merging. When present
        /// it folds same-issue groups together in the grouped listing; the deterministic grouping is
        /// unchanged when the decision is off or the model is unavailable.
        /// </param>
        public static void Register(RegisterToolDelegate register, DatabaseDriver database, PapercutMergeAdapter? mergeAdapter = null)
        {
            register(
                "armada_list_papercuts",
                "List friction that captains reported during missions, collapsed into groups of the same vessel, category, and problem. Use continuationToken to read later pages without changing filters. TotalFiltered and TotalGroups describe the scanned corpus; when ScanTruncated is true they are lower bounds because scanLimit stopped the source scan.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        vesselId = new { type = "string", description = "Filter to one vessel (vsl_ prefix)" },
                        category = new { type = "string", description = "Filter to one category: BriefContradiction, ToolFailure, MissingDoc, BrokenLink, RepoFriction, TestFlake, EnvSetup, PlatformBug, Other" },
                        minSeverity = new { type = "string", description = "Minimum severity: Low, Medium, or High" },
                        sinceHours = new { type = "integer", description = "Only include reports newer than this many hours" },
                        ungrouped = new { type = "boolean", description = "Return every report instead of groups (default false)" },
                        continuationToken = new { type = "string", description = "Opaque token from the previous page. Keep all filters and limits unchanged." },
                        limit = new { type = "integer", description = "Maximum rows returned (default 25, maximum 200)" },
                        scanLimit = new { type = "integer", description = "Stored reports to scan before filtering (default 500, maximum 5000)" }
                    },
                    required = new string[] { }
                },
                async (args) =>
                {
                    // The MCP handler delegate carries no transport token, so each call owns one bounded
                    // by a deadline. A listing whose merge decisions outrun it fails closed to the plain
                    // grouping rather than holding the tool call open.
                    using (System.Threading.CancellationTokenSource callToken = new System.Threading.CancellationTokenSource(_ListCallTimeout))
                    {
                        return await ListAsync(args, database, mergeAdapter, callToken.Token).ConfigureAwait(false);
                    }
                });
        }

        /// <summary>
        /// Run one <c>armada_list_papercuts</c> call. The token bounds the whole call, including every
        /// D6 merge decision the listing consults.
        /// </summary>
        /// <param name="args">Tool arguments.</param>
        /// <param name="database">Database driver for event data access.</param>
        /// <param name="mergeAdapter">The D6 merge adapter, or null.</param>
        /// <param name="token">The tool call's cancellation token.</param>
        /// <returns>The listing response.</returns>
        internal static async Task<object> ListAsync(JsonElement? args, DatabaseDriver database, PapercutMergeAdapter? mergeAdapter, System.Threading.CancellationToken token)
        {
            PapercutListArgs request = args == null
                ? new PapercutListArgs()
                : JsonSerializer.Deserialize<PapercutListArgs>(args.Value, _JsonOptions) ?? new PapercutListArgs();

            int scanLimit = Math.Clamp(request.ScanLimit ?? _DefaultScanLimit, 1, _MaxScanLimit);
            int limit = Math.Clamp(request.Limit ?? _DefaultLimit, 1, _MaxLimit);
            ContinuationState? continuation = DecodeContinuation(request.ContinuationToken);
            if (request.ContinuationToken != null && continuation == null)
                return (object)new { Error = "Invalid continuationToken" };

            PapercutCategoryEnum? categoryFilter = null;
            if (!String.IsNullOrWhiteSpace(request.Category))
            {
                PapercutCategoryEnum resolved = PapercutParser.ParseCategory(request.Category);
                if (resolved == PapercutCategoryEnum.Other &&
                    !String.Equals(request.Category!.Trim(), "Other", StringComparison.OrdinalIgnoreCase))
                    return (object)new { Error = "Unknown category: " + request.Category };
                categoryFilter = resolved;
            }

            PapercutSeverityEnum? severityFilter = null;
            if (!String.IsNullOrWhiteSpace(request.MinSeverity))
            {
                PapercutSeverityEnum resolvedSeverity;
                if (!Enum.TryParse<PapercutSeverityEnum>(request.MinSeverity!.Trim(), true, out resolvedSeverity))
                    return (object)new { Error = "Unknown severity: " + request.MinSeverity };
                severityFilter = resolvedSeverity;
            }

            DateTime? since = null;
            if (request.SinceHours.HasValue && request.SinceHours.Value > 0)
                since = continuation?.SinceUtc ?? DateTime.UtcNow.AddHours(-request.SinceHours.Value);
            else if (continuation?.SinceUtc.HasValue == true)
                return (object)new { Error = "continuationToken does not match the current filters and limits" };

            bool ungrouped = request.Ungrouped == true;
            DateTime snapshotUtc = continuation?.SnapshotUtc ?? DateTime.UtcNow;
            string filterSignature = BuildFilterSignature(request, scanLimit, limit, ungrouped, categoryFilter, severityFilter, since);
            if (continuation != null && !String.Equals(continuation.FilterSignature, filterSignature, StringComparison.Ordinal))
                return (object)new { Error = "continuationToken does not match the current filters and limits" };

            PapercutScanResult scan = await ScanEventsAsync(database, scanLimit, snapshotUtc, token).ConfigureAwait(false);
            string sourceFingerprint = Fingerprint(scan.Events
                .OrderByDescending(evt => evt.CreatedUtc).ThenByDescending(evt => evt.Id, StringComparer.Ordinal)
                .Select(evt => evt.Id));
            if (continuation != null && !String.Equals(continuation.SourceFingerprint, sourceFingerprint, StringComparison.Ordinal))
                return (object)new { Error = "The papercut scan changed while paging. Restart the listing." };

            List<Papercut> papercuts = new List<Papercut>();
            List<PapercutEventRow> papercutEvents = new List<PapercutEventRow>();
            foreach (ArmadaEvent evt in scan.Events)
            {
                Papercut? papercut = PapercutService.TryFromEvent(evt);
                if (papercut == null) continue;
                if (!String.IsNullOrWhiteSpace(request.VesselId) &&
                    !String.Equals(papercut.VesselId, request.VesselId, StringComparison.Ordinal)) continue;
                if (categoryFilter.HasValue && papercut.Category != categoryFilter.Value) continue;
                if (severityFilter.HasValue && papercut.Severity < severityFilter.Value) continue;
                if (since.HasValue && papercut.ReportedUtc < since.Value) continue;
                papercuts.Add(papercut);
                papercutEvents.Add(new PapercutEventRow { Papercut = papercut, EventId = evt.Id });
            }

            bool scanTruncated = scan.TotalEvents > scan.Events.Count;
            if (ungrouped)
            {
                List<PapercutEventRow> ordered = papercutEvents
                    .OrderByDescending(item => item.Papercut.ReportedUtc)
                    .ThenByDescending(item => item.EventId, StringComparer.Ordinal)
                    .ToList();
                string resultFingerprint = Fingerprint(ordered.Select(item => item.EventId));
                if (continuation != null && !String.Equals(continuation.ResultFingerprint, resultFingerprint, StringComparison.Ordinal))
                    return (object)new { Error = "The papercut results changed while paging. Restart the listing." };

                int offset = continuation?.Offset ?? 0;
                if (offset > ordered.Count || (continuation != null && offset > 0 &&
                    !String.Equals(ordered[offset - 1].EventId, continuation.LastKey, StringComparison.Ordinal)))
                    return (object)new { Error = "The continuation position is invalid. Restart the listing." };
                List<PapercutEventRow> page = ordered.Skip(offset).Take(limit).ToList();
                bool hasMore = offset + page.Count < ordered.Count;
                string? nextToken = hasMore ? EncodeContinuation(new ContinuationState
                {
                    FilterSignature = filterSignature,
                    SnapshotUtc = snapshotUtc,
                    SourceFingerprint = sourceFingerprint,
                    ResultFingerprint = resultFingerprint,
                    SinceUtc = since,
                    Offset = offset + page.Count,
                    LastKey = page.LastOrDefault()?.EventId ?? String.Empty
                }) : null;
                return (object)new
                {
                    Scanned = scan.Events.Count,
                    Matched = papercuts.Count,
                    TotalFiltered = papercuts.Count,
                    TotalEvents = scan.TotalEvents,
                    ScanTruncated = scanTruncated,
                    HasMore = hasMore,
                    NextContinuationToken = nextToken,
                    Papercuts = page.Select(item => item.Papercut).ToList()
                };
            }

            List<PapercutGroup> groups = PapercutService.Group(papercuts);
            if (mergeAdapter != null)
                groups = await mergeAdapter.MergeAsync(groups, token).ConfigureAwait(false);

            groups = groups.OrderByDescending(group => group.Count)
                .ThenByDescending(group => group.LastSeenUtc)
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();
            string groupFingerprint = FingerprintGroups(groups, papercutEvents);
            if (continuation != null && !String.Equals(continuation.ResultFingerprint, groupFingerprint, StringComparison.Ordinal))
                return (object)new { Error = "The grouped papercut results changed while paging. Restart the listing." };

            int groupOffset = continuation?.Offset ?? 0;
            if (groupOffset > groups.Count || (continuation != null && groupOffset > 0 &&
                !String.Equals(groups[groupOffset - 1].Key, continuation.LastKey, StringComparison.Ordinal)))
                return (object)new { Error = "The continuation position is invalid. Restart the listing." };
            List<PapercutGroup> groupPage = groups.Skip(groupOffset).Take(limit).ToList();
            bool groupsRemain = groupOffset + groupPage.Count < groups.Count;
            string? groupNextToken = groupsRemain ? EncodeContinuation(new ContinuationState
            {
                FilterSignature = filterSignature,
                SnapshotUtc = snapshotUtc,
                SourceFingerprint = sourceFingerprint,
                ResultFingerprint = groupFingerprint,
                SinceUtc = since,
                Offset = groupOffset + groupPage.Count,
                LastKey = groupPage.LastOrDefault()?.Key ?? String.Empty
            }) : null;

            return (object)new
            {
                Scanned = scan.Events.Count,
                Matched = papercuts.Count,
                GroupCount = groups.Count,
                TotalFiltered = papercuts.Count,
                TotalGroups = groups.Count,
                TotalEvents = scan.TotalEvents,
                ScanTruncated = scanTruncated,
                HasMore = groupsRemain,
                NextContinuationToken = groupNextToken,
                Groups = groupPage
            };
        }

        private static string BuildFilterSignature(PapercutListArgs request, int scanLimit, int limit, bool ungrouped,
            PapercutCategoryEnum? category, PapercutSeverityEnum? severity, DateTime? since)
        {
            return String.Join("|", request.VesselId ?? String.Empty, category?.ToString() ?? String.Empty,
                severity?.ToString() ?? String.Empty, request.SinceHours?.ToString() ?? "", since?.ToString("O") ?? "",
                ungrouped ? "1" : "0", scanLimit.ToString(), limit.ToString());
        }

        private static async Task<PapercutScanResult> ScanEventsAsync(DatabaseDriver database, int scanLimit, DateTime snapshotUtc, System.Threading.CancellationToken token)
        {
            const int pageSize = 1000;
            List<ArmadaEvent> events = new List<ArmadaEvent>();
            long totalEvents = 0;
            int pageNumber = 1;
            while (events.Count < scanLimit)
            {
                EnumerationQuery query = new EnumerationQuery
                {
                    EventType = PapercutParser.EventType,
                    CreatedBefore = snapshotUtc,
                    PageNumber = pageNumber,
                    PageSize = Math.Min(pageSize, scanLimit)
                };
                EnumerationResult<ArmadaEvent> result = await database.Events.EnumerateAsync(query, token).ConfigureAwait(false);
                totalEvents = result.TotalRecords;
                if (result.Objects.Count == 0) break;
                int remaining = scanLimit - events.Count;
                events.AddRange(result.Objects.Take(remaining));
                pageNumber++;
                if (result.Objects.Count < query.PageSize) break;
            }
            return new PapercutScanResult { Events = events, TotalEvents = totalEvents };
        }

        private static string Fingerprint(IEnumerable<string> values)
        {
            byte[] data = Encoding.UTF8.GetBytes(String.Join("\n", values));
            return Convert.ToHexString(SHA256.HashData(data));
        }

        private static string FingerprintGroups(IEnumerable<PapercutGroup> groups, IEnumerable<PapercutEventRow> reports)
        {
            Dictionary<string, List<string>> eventIdsByKey = reports
                .GroupBy(report => PapercutService.BuildKey(report.Papercut), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(report => report.EventId).OrderBy(id => id, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            List<string> fingerprints = new List<string>();
            foreach (PapercutGroup group in groups)
            {
                List<string> memberKeys = new List<string> { group.Key };
                memberKeys.AddRange(group.MergedGroupKeys);
                memberKeys = memberKeys.Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToList();
                List<string> memberIds = memberKeys
                    .Where(eventIdsByKey.ContainsKey)
                    .SelectMany(key => eventIdsByKey[key])
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                fingerprints.Add(group.Key + "|" + group.Count + "|" + group.LastSeenUtc.Ticks + "|" +
                    String.Join(",", memberKeys) + "|" + String.Join(",", memberIds));
            }
            return Fingerprint(fingerprints);
        }

        private static string EncodeContinuation(ContinuationState state)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state)));
        }

        private static ContinuationState? DecodeContinuation(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            try
            {
                ContinuationState? state = JsonSerializer.Deserialize<ContinuationState>(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
                return state != null && state.Version == 1 && state.Offset >= 0 ? state : null;
            }
            catch (FormatException) { return null; }
            catch (JsonException) { return null; }
        }

        private sealed class PapercutScanResult
        {
            public List<ArmadaEvent> Events { get; set; } = new List<ArmadaEvent>();
            public long TotalEvents { get; set; }
        }

        private sealed class PapercutEventRow
        {
            public Papercut Papercut { get; set; } = new Papercut();
            public string EventId { get; set; } = String.Empty;
        }

        private sealed class ContinuationState
        {
            public int Version { get; set; } = 1;
            public string FilterSignature { get; set; } = String.Empty;
            public DateTime SnapshotUtc { get; set; }
            public string SourceFingerprint { get; set; } = String.Empty;
            public string ResultFingerprint { get; set; } = String.Empty;
            public DateTime? SinceUtc { get; set; }
            public int Offset { get; set; }
            public string LastKey { get; set; } = String.Empty;
        }

    }
}
