namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Json;
    using Armada.Core.Models;
    using Armada.Core.Services.Archive;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Writes and reads trace records: one compact, payload-free description per objective, voyage, mission,
    /// check run, incident, merge entry and deployment, kept after the source row is expired so an operator
    /// can still walk from any id to the work around it. A record is built from the live source row and its
    /// events, and a write replaces the stored record of the same id, so every write path is idempotent.
    /// A record whose source row no longer exists is never overwritten: the stored copy is the only one left.
    /// While the archive is enabled and configured, a record whose described record has ended, or one written
    /// because data expiry is about to delete its row, is also uploaded to <c>trace/</c>; the verified upload
    /// is kept as an archive pointer on the record, and unchanged content is not uploaded again.
    /// </summary>
    public sealed class TraceRecorder
    {
        #region Public-Members

        /// <summary>Cursor the periodic sweep resumes from.</summary>
        public const string SweepCursorName = "sweep.events";

        /// <summary>Prefix of the per-table cursors the backfill resumes from; the table's kind follows it.</summary>
        public const string BackfillCursorPrefix = "backfill.";

        /// <summary>Look-back the sweep starts from when its cursor was never written; older history is the backfill's work.</summary>
        public static readonly TimeSpan InitialSweepLookBack = TimeSpan.FromDays(1);

        /// <summary>Default number of records a trace returns.</summary>
        public const int DefaultMaximumChainRecords = 60;

        /// <summary>Largest number of records a trace returns.</summary>
        public const int MaximumChainRecords = 200;

        /// <summary>Shortest id part, after the kind prefix, that a short-id lookup accepts.</summary>
        public const int MinimumShortIdLength = 4;

        /// <summary>
        /// Current archive settings. Null, or settings with the archive disabled or not configured, keeps trace
        /// records in the local table only.
        /// </summary>
        public Func<ArchiveSettings>? ArchiveSettingsProvider { get; set; } = null;

        /// <summary>
        /// Builds the archive sink from the current archive settings. Null keeps trace records in the local
        /// table only.
        /// </summary>
        public Func<ArchiveSettings, IArchiveSink>? ArchiveSinkFactory { get; set; } = null;

        #endregion

        #region Private-Members

        private readonly string _Header = "[TraceRecorder] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule _Logging;
        private int _ArchiveOffLogged = 0;
        private const long _MaxArchivedRecordBytes = 4 * 1024 * 1024;
        private readonly IncidentService _Incidents;
        private const int _EventPageSize = 1000;
        private const int _EntityEventLimit = 1000;
        private const int _ExpiryBatchSize = 500;
        private const int _ChildLimit = 200;
        private const int _BackfillPageSize = 500;

        private static readonly TraceEntityKindEnum[] _BackfillOrder = new[]
        {
            TraceEntityKindEnum.Objective,
            TraceEntityKindEnum.Voyage,
            TraceEntityKindEnum.Mission,
            TraceEntityKindEnum.MergeEntry,
            TraceEntityKindEnum.Deployment,
            TraceEntityKindEnum.Incident
        };

        private static readonly string[] _IdPrefixes = new[] { "msn_", "vyg_", "obj_", "chk_", "inc_", "mrg_", "dpl_" };

        private static readonly HashSet<string> _NoiseEventTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "mission.token_usage",
            "mission.launch_prompt_budget",
            "mission.prompt_budget",
            "mission.context_pack_usage",
            "mission.assignment_held",
            "mission.assignment_hold_released"
        };

        private static readonly string[] _NoiseEventPrefixes = new[] { "typed_decision.", "objective_scheduler.", "routing." };

        private static readonly string[] _TimelineMarkers = new[]
        {
            "created", "changed", "dispatched", "assigned", "started", "complete", "failed", "cancelled",
            "landed", "landing", "restarted", "requeued", "redispatched", "review_", "definition_of_done_evaluated",
            "hold_engaged", "hold_cleared", "work_produced", "noop_already_integrated", "pull_request",
            "judge_challenge", "rescue", "snapshot", "deleted", "pipeline_recorded", "stage_skipped", "progress",
            "push_failed", "target_advanced", "closed", "opened", "verified", "rolled_back", "approved"
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module.</param>
        public TraceRecorder(DatabaseDriver database, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Incidents = new IncidentService(database);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Resolve the record kind a full id names from its prefix.
        /// </summary>
        /// <param name="id">Record id.</param>
        /// <param name="kind">Resolved kind.</param>
        /// <returns>True when the prefix names a traced kind.</returns>
        public static bool TryResolveKind(string? id, out TraceEntityKindEnum kind)
        {
            kind = TraceEntityKindEnum.Mission;
            if (String.IsNullOrWhiteSpace(id)) return false;
            string trimmed = id.Trim();
            if (trimmed.StartsWith("msn_", StringComparison.Ordinal)) { kind = TraceEntityKindEnum.Mission; return true; }
            if (trimmed.StartsWith("vyg_", StringComparison.Ordinal)) { kind = TraceEntityKindEnum.Voyage; return true; }
            if (trimmed.StartsWith("obj_", StringComparison.Ordinal)) { kind = TraceEntityKindEnum.Objective; return true; }
            if (trimmed.StartsWith("chk_", StringComparison.Ordinal)) { kind = TraceEntityKindEnum.CheckRun; return true; }
            if (trimmed.StartsWith("inc_", StringComparison.Ordinal)) { kind = TraceEntityKindEnum.Incident; return true; }
            if (trimmed.StartsWith("mrg_", StringComparison.Ordinal)) { kind = TraceEntityKindEnum.MergeEntry; return true; }
            if (trimmed.StartsWith("dpl_", StringComparison.Ordinal)) { kind = TraceEntityKindEnum.Deployment; return true; }
            return false;
        }

        /// <summary>
        /// Build the trace record of one record from its live source row and store it, replacing any stored
        /// record of the same id. When the source row no longer exists the stored record is left unchanged.
        /// </summary>
        /// <param name="kind">Record kind.</param>
        /// <param name="id">Record id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored record, or null when the source row does not exist.</returns>
        public Task<TraceRecord?> RecordAsync(TraceEntityKindEnum kind, string id, CancellationToken token = default)
        {
            return RecordAsync(kind, id, false, token);
        }

        /// <summary>
        /// Build and store the trace record of one record, as <see cref="RecordAsync(TraceEntityKindEnum, string, CancellationToken)"/>
        /// does. With <paramref name="beforeExpiry"/> the record is uploaded to the archive even when the described
        /// record has not ended, because its row is about to be deleted.
        /// </summary>
        /// <param name="kind">Record kind.</param>
        /// <param name="id">Record id.</param>
        /// <param name="beforeExpiry">True when data expiry is about to delete the source row.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored record, or null when the source row does not exist.</returns>
        public async Task<TraceRecord?> RecordAsync(TraceEntityKindEnum kind, string id, bool beforeExpiry, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            TraceDocument? document = await BuildAsync(kind, id.Trim(), token).ConfigureAwait(false);
            if (document == null) return null;

            TraceRecord? existing = await _Database.TraceRecords.ReadAsync(document.Id, token).ConfigureAwait(false);
            if (existing != null && existing.Document.Archive.Count > 0 && document.Archive.Count == 0)
                document.Archive = existing.Document.Archive;
            if (beforeExpiry || document.CompletedUtc.HasValue)
                await UploadAsync(document, token).ConfigureAwait(false);

            TraceRecord record = new TraceRecord
            {
                Id = document.Id,
                Kind = document.Kind,
                TenantId = document.TenantId,
                Status = document.Status,
                SubjectCreatedUtc = document.CreatedUtc,
                SubjectLastUpdateUtc = document.LastUpdateUtc,
                RecordedUtc = DateTime.UtcNow,
                Document = document
            };
            await _Database.TraceRecords.UpsertAsync(record, token).ConfigureAwait(false);
            return record;
        }

        /// <summary>
        /// Record every record named by events newer than the sweep cursor, then advance the cursor. The first
        /// sweep starts <see cref="InitialSweepLookBack"/> before now.
        /// </summary>
        /// <param name="maximumEvents">Most events one call reads.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Counts.</returns>
        public Task<TraceSweepResult> SweepAsync(int maximumEvents, CancellationToken token = default)
        {
            return ProcessEventsAsync(SweepCursorName, DateTime.UtcNow - InitialSweepLookBack, maximumEvents, token);
        }

        /// <summary>
        /// Record stored history, one bounded batch per call. The batch walks the objective, voyage, mission,
        /// merge entry, deployment and incident tables in creation order, each from its own cursor (starting at
        /// <paramref name="sinceUtc"/> when the cursor was never written), and records each row with the check
        /// runs, merge entries and deployments of each mission and voyage. Repeated calls resume where the last
        /// one stopped; a call after <see cref="TraceBackfillResult.Done"/> visits only rows created since.
        /// </summary>
        /// <param name="sinceUtc">Oldest creation time to start a table from when its cursor was never written.</param>
        /// <param name="maximumRows">Most source rows one call visits.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Counts.</returns>
        public async Task<TraceBackfillResult> BackfillAsync(DateTime sinceUtc, int maximumRows, CancellationToken token = default)
        {
            DateTime since = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc);
            TraceBackfillResult result = new TraceBackfillResult { SinceUtc = since };
            TraceSweepResult counts = new TraceSweepResult();
            HashSet<string> recorded = new HashSet<string>(StringComparer.Ordinal);
            int budget = Math.Max(1, maximumRows);
            bool allDone = true;

            foreach (TraceEntityKindEnum kind in _BackfillOrder)
            {
                string cursorName = BackfillCursorPrefix + kind;
                DateTime cursor = await ReadCursorAsync(cursorName, since, token).ConfigureAwait(false);
                int visited = 0;
                bool kindDone = false;
                while (budget > 0)
                {
                    int pageSize = Math.Min(_BackfillPageSize, budget);
                    List<TraceSourceRow> rows = await ReadSourcePageAsync(kind, cursor, pageSize, token).ConfigureAwait(false);
                    foreach (TraceSourceRow row in rows)
                    {
                        visited++;
                        budget--;
                        if (!recorded.Add(kind + ":" + row.Id)) continue;
                        await RecordWithChildrenAsync(kind, row.Id, recorded, counts, token).ConfigureAwait(false);
                    }
                    if (rows.Count > 0)
                    {
                        cursor = NextCursor(rows[0].CreatedUtc, rows[rows.Count - 1].CreatedUtc, cursor);
                        await _Database.TraceRecords.WriteCursorAsync(cursorName, cursor.ToString("o", CultureInfo.InvariantCulture), token).ConfigureAwait(false);
                    }
                    if (rows.Count < pageSize)
                    {
                        kindDone = true;
                        break;
                    }
                }
                result.RowsByKind[kind.ToString()] = visited;
                result.Cursors[kind.ToString()] = cursor;
                result.RowsRead += visited;
                if (!kindDone) allDone = false;
            }

            result.RecordsWritten = counts.RecordsWritten;
            result.RecordsMissing = counts.RecordsMissing;
            result.Failures = counts.Failures;
            result.Done = allDone;
            result.StoredRecords = await _Database.TraceRecords.CountAsync(token).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Write the trace record of one record that data expiry is about to remove evidence of, when the
        /// trace table holds no record of it yet. Failures are thrown, so the caller can stop the expiry.
        /// </summary>
        /// <param name="kind">Record kind.</param>
        /// <param name="id">Record id.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The written record, or null when one was already stored or the source row does not exist.</returns>
        public async Task<TraceRecord?> RecordUntracedBeforeExpiryAsync(TraceEntityKindEnum kind, string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            if (await _Database.TraceRecords.ReadAsync(id.Trim(), token).ConfigureAwait(false) != null) return null;
            return await RecordAsync(kind, id, true, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete trace records whose described record last changed more than <paramref name="retentionDays"/> ago.
        /// </summary>
        /// <param name="retentionDays">Trace retention in days; zero or less keeps every record.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Records deleted.</returns>
        public async Task<int> ExpireAsync(int retentionDays, CancellationToken token = default)
        {
            if (retentionDays <= 0) return 0;
            DateTime cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            int deleted = 0;
            while (true)
            {
                List<string> ids = await _Database.TraceRecords.EnumerateIdsLastUpdatedBeforeAsync(cutoff, _ExpiryBatchSize, token).ConfigureAwait(false);
                if (ids.Count == 0) break;
                int removed = await _Database.TraceRecords.DeleteAsync(ids, token).ConfigureAwait(false);
                deleted += removed;
                if (removed == 0 || ids.Count < _ExpiryBatchSize) break;
            }
            if (deleted > 0) _Logging.Info(_Header + "expired " + deleted + " trace records older than " + retentionDays + " days");
            return deleted;
        }

        /// <summary>
        /// Return the linked chain around one id. The id may be a full id of any traced kind or a short id: a
        /// prefix of a full id, with or without its kind prefix. A record missing from the trace table is built
        /// from its live source row and stored. The walk follows each record's links and the links other
        /// records hold to it, breadth first.
        /// </summary>
        /// <param name="id">Full or short id.</param>
        /// <param name="maximumRecords">Most records returned.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The chain.</returns>
        public async Task<TraceChain> TraceAsync(string id, int maximumRecords = DefaultMaximumChainRecords, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            int bound = Math.Clamp(maximumRecords, 1, MaximumChainRecords);
            TraceChain chain = new TraceChain { RequestedId = id.Trim() };

            string? resolved = await ResolveIdAsync(chain, token).ConfigureAwait(false);
            if (resolved == null) return chain;
            chain.ResolvedId = resolved;

            Queue<string> queue = new Queue<string>();
            HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
            queue.Enqueue(resolved);
            visited.Add(resolved);
            bool first = true;
            while (queue.Count > 0)
            {
                if (chain.Records.Count >= bound)
                {
                    chain.Truncated = true;
                    break;
                }

                string current = queue.Dequeue();
                TraceDocument? document = null;
                TraceRecord? stored = await _Database.TraceRecords.ReadAsync(current, token).ConfigureAwait(false);
                // A stored projection can lag completion, verdicts and linked checks until the next
                // sweep. Read current facts without writing or uploading archives on an operator read.
                if (TryResolveKind(current, out TraceEntityKindEnum kind))
                    document = await BuildAsync(kind, current, token).ConfigureAwait(false);
                if (document != null)
                {
                    if (stored != null)
                    {
                        TraceDocument history = stored.Document;
                        if (document.Archive.Count == 0)
                            document.Archive = new List<TraceArchivePointer>(history.Archive);
                        // Raw children and events can expire while their parent remains live. Keep
                        // historical edges and events so the walk can still reach their archives.
                        foreach (TraceLink link in history.Links) AddLink(document, link.Kind, link.Id, link.Relation);
                        document.Timeline = history.Timeline.Concat(document.Timeline)
                            .GroupBy(entry => (entry.Event, entry.Utc)).Select(group => group.Last())
                            .OrderBy(entry => entry.Utc).TakeLast(TraceDocument.MaximumTimelineEntries).ToList();
                        // Derived proof is applicable only to the same attempt and exact commit.
                        bool sameAttempt = !String.IsNullOrWhiteSpace(document.CommitSha)
                            && String.Equals(document.CommitSha, history.CommitSha, StringComparison.Ordinal)
                            && document.StartedUtc == history.StartedUtc
                            && String.Equals(document.BranchName, history.BranchName, StringComparison.Ordinal);
                        if (sameAttempt)
                        {
                            if (document.LandedSha == null) document.LandedSha = history.LandedSha;
                            if (document.Gate == null && String.Equals(history.Gate?.CommitSha, document.CommitSha, StringComparison.Ordinal))
                                document.Gate = history.Gate;
                        }
                    }
                    if (first) chain.Source = "live";
                }
                else if (stored != null)
                {
                    document = stored.Document;
                    if (first) chain.Source = "table";
                }
                bool requested = first;
                first = false;

                if (document == null)
                {
                    chain.MissingIds.Add(current);
                    string? archivedKey = await FindArchivedKeyAsync(current, token).ConfigureAwait(false);
                    if (archivedKey == null) continue;
                    chain.ArchivedKeys.Add(archivedKey);
                    document = await ReadArchivedAsync(archivedKey, token).ConfigureAwait(false);
                    if (document == null) continue;
                    if (requested) chain.Source = "archive";
                }
                chain.Records.Add(document);

                foreach (TraceLink link in document.Links)
                {
                    if (!String.IsNullOrWhiteSpace(link.Id) && visited.Add(link.Id)) queue.Enqueue(link.Id);
                }
                List<TraceLink> inbound = await _Database.TraceRecords.EnumerateLinksToAsync(current, _ChildLimit, token).ConfigureAwait(false);
                foreach (TraceLink link in inbound)
                {
                    if (!String.IsNullOrWhiteSpace(link.Id) && visited.Add(link.Id)) queue.Enqueue(link.Id);
                }
            }
            return chain;
        }

        #endregion

        #region Private-Methods

        private ArchiveSettings? ActiveArchive()
        {
            ArchiveSettings? archive = ArchiveSettingsProvider?.Invoke();
            if (archive == null || !archive.Enabled || !archive.IsConfigured || ArchiveSinkFactory == null)
            {
                // Logged once each time the archive goes off, never once per record.
                if (Interlocked.Exchange(ref _ArchiveOffLogged, 1) == 0)
                    _Logging.Info(_Header + "the archive is off; trace records stay in the local table only");
                return null;
            }
            Interlocked.Exchange(ref _ArchiveOffLogged, 0);
            return archive;
        }

        private async Task<TraceDocument?> ReadArchivedAsync(string key, CancellationToken token)
        {
            ArchiveSettings? archive = ActiveArchive();
            if (archive == null) return null;
            try
            {
                byte[]? content = await ArchiveSinkFactory!(archive).GetBytesAsync(key, _MaxArchivedRecordBytes, token).ConfigureAwait(false);
                if (content == null) return null;
                return JsonSerializer.Deserialize<TraceDocument>(content, JsonDefaults.Insensitive);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "trace archive read failed for " + key + ": " + ex.Message);
                return null;
            }
        }

        private async Task UploadAsync(TraceDocument document, CancellationToken token)
        {
            ArchiveSettings? archive = ActiveArchive();
            if (archive == null) return;

            string key = ArchiveKeys.Trace(archive.KeyPrefix, document.CreatedUtc, document.Id);
            TraceArchivePointer? previous = document.Archive.FirstOrDefault(pointer => String.Equals(pointer.Key, key, StringComparison.Ordinal));
            List<TraceArchivePointer> others = document.Archive.Where(pointer => !String.Equals(pointer.Key, key, StringComparison.Ordinal)).ToList();

            // The uploaded copy carries every pointer except its own, so its hash is stable across rewrites
            // and an unchanged record is not uploaded again.
            document.Archive = others;
            byte[] content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document));
            string sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            TraceArchivePointer pointer = previous ?? new TraceArchivePointer { Key = key };
            if (previous == null || !String.Equals(previous.Sha256, sha256, StringComparison.Ordinal))
            {
                try
                {
                    IArchiveSink sink = ArchiveSinkFactory!(archive);
                    ArchivePutResult put = await sink.PutBytesAsync(key, content, "application/json", token).ConfigureAwait(false);
                    if (put.Verified)
                    {
                        pointer = new TraceArchivePointer { Key = key, Sha256 = put.Sha256, Bytes = put.Bytes, Expired = false };
                    }
                    else
                    {
                        _Logging.Warn(_Header + "trace upload not verified for " + document.Id + ": " + put.FailureCode + " " + put.FailureMessage);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "trace upload failed for " + document.Id + ": " + ex.Message);
                }
            }

            document.Archive = others;
            if (!String.IsNullOrWhiteSpace(pointer.Sha256)) document.Archive.Add(pointer);
        }

        private async Task<string?> FindArchivedKeyAsync(string id, CancellationToken token)
        {
            ArchiveSettings? archive = ActiveArchive();
            if (archive == null) return null;
            try
            {
                IArchiveSink sink = ArchiveSinkFactory!(archive);
                List<ArchiveObjectInfo> objects = await sink.ListAsync(ArchiveKeys.FolderPrefix(archive.KeyPrefix, ArchiveKeys.TraceFolder), token).ConfigureAwait(false);
                ArchiveObjectInfo? match = objects.FirstOrDefault(item => item.Key.EndsWith("/" + id + ".json", StringComparison.Ordinal));
                return match?.Key;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "trace archive lookup failed for " + id + ": " + ex.Message);
                return null;
            }
        }

        private async Task<string?> ResolveIdAsync(TraceChain chain, CancellationToken token)
        {
            string requested = chain.RequestedId;
            if (await _Database.TraceRecords.ReadAsync(requested, token).ConfigureAwait(false) != null) return requested;
            if (TryResolveKind(requested, out TraceEntityKindEnum kind)
                && await SourceExistsAsync(kind, requested, token).ConfigureAwait(false))
                return requested;

            List<string> prefixes = new List<string>();
            if (TryResolveKind(requested, out TraceEntityKindEnum _))
            {
                if (requested.Length - 4 >= MinimumShortIdLength) prefixes.Add(requested);
            }
            else if (requested.Length >= MinimumShortIdLength)
            {
                foreach (string prefix in _IdPrefixes) prefixes.Add(prefix + requested);
            }

            List<string> matches = new List<string>();
            foreach (string prefix in prefixes)
            {
                List<string> found = await _Database.TraceRecords.EnumerateIdsByPrefixAsync(prefix, 11, token).ConfigureAwait(false);
                foreach (string match in found)
                {
                    if (!matches.Contains(match, StringComparer.Ordinal)) matches.Add(match);
                }
            }

            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1)
            {
                chain.Candidates = matches.Take(10).ToList();
                return null;
            }

            // A full id whose record and source row are both gone still resolves when the archive holds it.
            if (TryResolveKind(requested, out TraceEntityKindEnum _) && await FindArchivedKeyAsync(requested, token).ConfigureAwait(false) != null)
                return requested;
            return null;
        }

        private async Task<bool> SourceExistsAsync(TraceEntityKindEnum kind, string id, CancellationToken token)
        {
            switch (kind)
            {
                case TraceEntityKindEnum.Mission: return await _Database.Missions.ExistsAsync(id, token).ConfigureAwait(false);
                case TraceEntityKindEnum.Voyage: return await _Database.Voyages.ReadAsync(id, token).ConfigureAwait(false) != null;
                case TraceEntityKindEnum.Objective: return await _Database.Objectives.ReadAsync(id, token).ConfigureAwait(false) != null;
                case TraceEntityKindEnum.CheckRun: return await _Database.CheckRuns.ReadAsync(id, null, token).ConfigureAwait(false) != null;
                case TraceEntityKindEnum.MergeEntry: return await _Database.MergeEntries.ReadAsync(id, token).ConfigureAwait(false) != null;
                case TraceEntityKindEnum.Deployment: return await _Database.Deployments.ReadAsync(id, null, token).ConfigureAwait(false) != null;
                case TraceEntityKindEnum.Incident: return await _Incidents.ReadAsync(SystemAuth(), id, token).ConfigureAwait(false) != null;
                default: return false;
            }
        }

        private async Task<TraceSweepResult> ProcessEventsAsync(string cursorName, DateTime defaultStartUtc, int maximumEvents, CancellationToken token)
        {
            TraceSweepResult result = new TraceSweepResult { CursorName = cursorName };
            int budget = Math.Max(1, maximumEvents);
            DateTime cursor = await ReadCursorAsync(cursorName, defaultStartUtc, token).ConfigureAwait(false);
            result.StartUtc = cursor;

            HashSet<string> recorded = new HashSet<string>(StringComparer.Ordinal);
            while (budget > 0)
            {
                EnumerationQuery query = new EnumerationQuery
                {
                    CreatedAfter = cursor,
                    Order = EnumerationOrderEnum.CreatedAscending,
                    PageNumber = 1,
                    PageSize = Math.Min(_EventPageSize, budget)
                };
                EnumerationResult<ArmadaEvent> page = await _Database.Events.EnumerateAsync(query, token).ConfigureAwait(false);
                List<ArmadaEvent> events = page.Objects ?? new List<ArmadaEvent>();
                result.EventsRead += events.Count;
                budget -= events.Count;

                List<TraceLink> named = new List<TraceLink>();
                foreach (ArmadaEvent evt in events)
                {
                    if (IsNoise(evt.EventType)) continue;
                    AddNamedRecords(evt, named);
                }

                foreach (TraceLink key in named)
                {
                    if (!recorded.Add(key.Kind + ":" + key.Id)) continue;
                    result.RecordsSeen++;
                    await RecordWithChildrenAsync(key.Kind, key.Id, recorded, result, token).ConfigureAwait(false);
                }

                if (events.Count > 0)
                {
                    cursor = NextCursor(events[0].CreatedUtc, events[events.Count - 1].CreatedUtc, cursor);
                    await _Database.TraceRecords.WriteCursorAsync(cursorName, cursor.ToString("o", CultureInfo.InvariantCulture), token).ConfigureAwait(false);
                }

                if (events.Count < query.PageSize)
                {
                    result.Done = true;
                    break;
                }
            }

            result.CursorUtc = cursor;
            result.StoredRecords = await _Database.TraceRecords.CountAsync(token).ConfigureAwait(false);
            return result;
        }

        private static DateTime NextCursor(DateTime firstOnPage, DateTime lastOnPage, DateTime previous)
        {
            // The next page reads strictly after the cursor. Stepping back one tick re-reads rows that share
            // the last timestamp, which is harmless because every write is idempotent; a page whose rows all
            // share one timestamp moves past it, so a batch always progresses.
            DateTime last = DateTime.SpecifyKind(lastOnPage, DateTimeKind.Utc);
            DateTime next = lastOnPage > firstOnPage ? last.AddTicks(-1) : last;
            return next <= previous ? last : next;
        }

        private async Task<List<TraceSourceRow>> ReadSourcePageAsync(TraceEntityKindEnum kind, DateTime afterUtc, int limit, CancellationToken token)
        {
            EnumerationQuery query = new EnumerationQuery
            {
                CreatedAfter = afterUtc,
                Order = EnumerationOrderEnum.CreatedAscending,
                PageNumber = 1,
                PageSize = limit
            };
            switch (kind)
            {
                case TraceEntityKindEnum.Voyage:
                    return (await _Database.Voyages.EnumerateAsync(query, token).ConfigureAwait(false)).Objects
                        .Select(item => new TraceSourceRow { Id = item.Id, CreatedUtc = item.CreatedUtc }).ToList();
                case TraceEntityKindEnum.Mission:
                    return (await _Database.Missions.EnumerateMissionSummariesAsync(query, token).ConfigureAwait(false)).Objects
                        .Select(item => new TraceSourceRow { Id = item.Id, CreatedUtc = item.CreatedUtc }).ToList();
                case TraceEntityKindEnum.MergeEntry:
                    return (await _Database.MergeEntries.EnumerateAsync(query, token).ConfigureAwait(false)).Objects
                        .Select(item => new TraceSourceRow { Id = item.Id, CreatedUtc = item.CreatedUtc }).ToList();
                case TraceEntityKindEnum.Objective:
                    return After((await _Database.Objectives.EnumerateAsync(token).ConfigureAwait(false))
                        .Select(item => new TraceSourceRow { Id = item.Id, CreatedUtc = item.CreatedUtc }), afterUtc, limit);
                case TraceEntityKindEnum.Deployment:
                    return After((await _Database.Deployments.EnumerateAllAsync(new DeploymentQuery(), token).ConfigureAwait(false))
                        .Select(item => new TraceSourceRow { Id = item.Id, CreatedUtc = item.CreatedUtc }), afterUtc, limit);
                case TraceEntityKindEnum.Incident:
                    return After((await ReadAllIncidentsAsync(token).ConfigureAwait(false))
                        .Select(item => new TraceSourceRow { Id = item.Id, CreatedUtc = item.DetectedUtc }), afterUtc, limit);
                default:
                    return new List<TraceSourceRow>();
            }
        }

        private static List<TraceSourceRow> After(IEnumerable<TraceSourceRow> rows, DateTime afterUtc, int limit)
        {
            return rows
                .Where(row => DateTime.SpecifyKind(row.CreatedUtc, DateTimeKind.Utc) > afterUtc)
                .OrderBy(row => row.CreatedUtc)
                .ThenBy(row => row.Id, StringComparer.Ordinal)
                .Take(limit)
                .ToList();
        }

        private async Task<List<Incident>> ReadAllIncidentsAsync(CancellationToken token)
        {
            List<Incident> incidents = new List<Incident>();
            IncidentQuery query = new IncidentQuery { PageNumber = 1, PageSize = 500, OldestFirst = true };
            for (int page = 0; page < 1000; page++)
            {
                EnumerationResult<Incident> result = await _Incidents.EnumerateAsync(SystemAuth(), query, token).ConfigureAwait(false);
                incidents.AddRange(result.Objects);
                if (result.Objects.Count == 0 || query.PageNumber >= result.TotalPages) break;
                query.PageNumber++;
            }
            return incidents;
        }

        private async Task RecordWithChildrenAsync(TraceEntityKindEnum kind, string id, HashSet<string> recorded, TraceSweepResult result, CancellationToken token)
        {
            TraceRecord? record = await TryRecordAsync(kind, id, result, token).ConfigureAwait(false);
            if (record == null) return;
            if (kind != TraceEntityKindEnum.Mission && kind != TraceEntityKindEnum.Voyage) return;

            foreach (TraceLink link in record.Document.Links)
            {
                if (link.Kind != TraceEntityKindEnum.CheckRun
                    && link.Kind != TraceEntityKindEnum.MergeEntry
                    && link.Kind != TraceEntityKindEnum.Deployment) continue;
                if (!recorded.Add(link.Kind + ":" + link.Id)) continue;
                await TryRecordAsync(link.Kind, link.Id, result, token).ConfigureAwait(false);
            }
        }

        private async Task<TraceRecord?> TryRecordAsync(TraceEntityKindEnum kind, string id, TraceSweepResult result, CancellationToken token)
        {
            try
            {
                TraceRecord? record = await RecordAsync(kind, id, token).ConfigureAwait(false);
                if (record == null) result.RecordsMissing++;
                else result.RecordsWritten++;
                return record;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Failures++;
                _Logging.Warn(_Header + "trace record write failed for " + kind + " " + id + ": " + ex.Message);
                return null;
            }
        }

        private async Task<DateTime> ReadCursorAsync(string cursorName, DateTime defaultStartUtc, CancellationToken token)
        {
            string? stored = await _Database.TraceRecords.ReadCursorAsync(cursorName, token).ConfigureAwait(false);
            if (!String.IsNullOrWhiteSpace(stored)
                && DateTime.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return defaultStartUtc;
        }

        private static void AddNamedRecords(ArmadaEvent evt, List<TraceLink> named)
        {
            if (TryResolveEntityType(evt.EntityType, out TraceEntityKindEnum entityKind) && !String.IsNullOrWhiteSpace(evt.EntityId))
                named.Add(new TraceLink { Kind = entityKind, Id = evt.EntityId! });
            if (!String.IsNullOrWhiteSpace(evt.MissionId))
                named.Add(new TraceLink { Kind = TraceEntityKindEnum.Mission, Id = evt.MissionId! });
            if (!String.IsNullOrWhiteSpace(evt.VoyageId))
                named.Add(new TraceLink { Kind = TraceEntityKindEnum.Voyage, Id = evt.VoyageId! });
        }

        private static bool TryResolveEntityType(string? entityType, out TraceEntityKindEnum kind)
        {
            kind = TraceEntityKindEnum.Mission;
            switch (entityType)
            {
                case "mission": kind = TraceEntityKindEnum.Mission; return true;
                case "voyage": kind = TraceEntityKindEnum.Voyage; return true;
                case "objective": kind = TraceEntityKindEnum.Objective; return true;
                case "check_run": kind = TraceEntityKindEnum.CheckRun; return true;
                case "incident": kind = TraceEntityKindEnum.Incident; return true;
                case "merge_entry": kind = TraceEntityKindEnum.MergeEntry; return true;
                case "deployment": kind = TraceEntityKindEnum.Deployment; return true;
                default: return false;
            }
        }

        private static string? EntityTypeName(TraceEntityKindEnum kind)
        {
            switch (kind)
            {
                case TraceEntityKindEnum.Mission: return "mission";
                case TraceEntityKindEnum.Voyage: return "voyage";
                case TraceEntityKindEnum.Objective: return "objective";
                case TraceEntityKindEnum.CheckRun: return "check_run";
                case TraceEntityKindEnum.Incident: return IncidentService.IncidentEntityType;
                case TraceEntityKindEnum.MergeEntry: return "merge_entry";
                case TraceEntityKindEnum.Deployment: return "deployment";
                default: return null;
            }
        }

        private static bool IsNoise(string? eventType)
        {
            if (String.IsNullOrWhiteSpace(eventType)) return true;
            if (_NoiseEventTypes.Contains(eventType)) return true;
            foreach (string prefix in _NoiseEventPrefixes)
            {
                if (eventType.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool IsTimelineEvent(string? eventType)
        {
            if (IsNoise(eventType)) return false;
            foreach (string marker in _TimelineMarkers)
            {
                if (eventType!.Contains(marker, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private async Task<TraceDocument?> BuildAsync(TraceEntityKindEnum kind, string id, CancellationToken token)
        {
            switch (kind)
            {
                case TraceEntityKindEnum.Mission: return await BuildMissionAsync(id, token).ConfigureAwait(false);
                case TraceEntityKindEnum.Voyage: return await BuildVoyageAsync(id, token).ConfigureAwait(false);
                case TraceEntityKindEnum.Objective: return await BuildObjectiveAsync(id, token).ConfigureAwait(false);
                case TraceEntityKindEnum.CheckRun: return await BuildCheckRunAsync(id, token).ConfigureAwait(false);
                case TraceEntityKindEnum.Incident: return await BuildIncidentAsync(id, token).ConfigureAwait(false);
                case TraceEntityKindEnum.MergeEntry: return await BuildMergeEntryAsync(id, token).ConfigureAwait(false);
                case TraceEntityKindEnum.Deployment: return await BuildDeploymentAsync(id, token).ConfigureAwait(false);
                default: return null;
            }
        }

        private async Task<TraceDocument?> BuildMissionAsync(string id, CancellationToken token)
        {
            Mission? mission = await _Database.Missions.ReadAsync(id, token).ConfigureAwait(false);
            if (mission == null) return null;

            TraceDocument document = new TraceDocument
            {
                Id = mission.Id,
                Kind = TraceEntityKindEnum.Mission,
                Title = Bound(mission.Title, TraceDocument.MaximumTitleLength),
                Status = mission.Status.ToString(),
                TenantId = mission.TenantId,
                CreatedUtc = mission.CreatedUtc,
                StartedUtc = mission.StartedUtc,
                CompletedUtc = mission.CompletedUtc,
                LastUpdateUtc = mission.LastUpdateUtc,
                VesselId = mission.VesselId,
                CaptainId = mission.CaptainId,
                Persona = mission.Persona,
                StageOrder = mission.StageOrder,
                BranchName = mission.BranchName,
                StartRef = mission.StartFromRef,
                CommitSha = mission.CommitHash,
                FailureReason = Bound(mission.FailureReason, TraceDocument.MaximumReasonLength),
                FinalMessage = Tail(mission.AgentOutput, TraceDocument.MaximumFinalMessageLength)
            };

            if (!String.IsNullOrWhiteSpace(mission.CaptainId))
            {
                Captain? captain = await _Database.Captains.ReadAsync(mission.CaptainId!, token).ConfigureAwait(false);
                if (captain != null) document.Model = captain.Model;
            }
            if (String.IsNullOrWhiteSpace(document.Model)) document.Model = mission.PreferredModel;

            if (String.Equals(mission.Persona, "Judge", StringComparison.OrdinalIgnoreCase))
            {
                document.Verdict = JudgeOutputParser.ParseVerdictLabel(mission);
                document.FindingCount = JudgeChallenge.ExtractFindings(mission.AgentOutput, out bool _).Count;
            }

            AddLink(document, TraceEntityKindEnum.Voyage, mission.VoyageId, "voyage");
            AddLink(document, TraceEntityKindEnum.Mission, mission.ParentMissionId, "parent");
            AddLink(document, TraceEntityKindEnum.Mission, mission.DependsOnMissionId, "depends_on");

            List<MissionSummary> children = await _Database.Missions.EnumerateMissionSummariesByParentAsync(mission.Id, token).ConfigureAwait(false);
            foreach (MissionSummary child in children.Take(_ChildLimit)) AddLink(document, TraceEntityKindEnum.Mission, child.Id, "child");

            EnumerationResult<MergeEntry> merges = await _Database.MergeEntries.EnumerateAsync(
                new EnumerationQuery { MissionId = mission.Id, PageNumber = 1, PageSize = _ChildLimit }, token).ConfigureAwait(false);
            foreach (MergeEntry merge in merges.Objects)
            {
                AddLink(document, TraceEntityKindEnum.MergeEntry, merge.Id, "merge_entry");
                if (merge.Status == MergeStatusEnum.Landed && !String.IsNullOrWhiteSpace(mission.CommitHash))
                    document.LandedSha = mission.CommitHash;
            }

            EnumerationResult<CheckRun> checks = await _Database.CheckRuns.EnumerateAsync(
                new CheckRunQuery { MissionId = mission.Id, PageNumber = 1, PageSize = _ChildLimit, IncludeOutput = false }, token).ConfigureAwait(false);
            foreach (CheckRun check in checks.Objects) AddLink(document, TraceEntityKindEnum.CheckRun, check.Id, "check");

            EnumerationResult<Deployment> deployments = await _Database.Deployments.EnumerateAsync(
                new DeploymentQuery { MissionId = mission.Id, PageNumber = 1, PageSize = _ChildLimit }, token).ConfigureAwait(false);
            foreach (Deployment deployment in deployments.Objects) AddLink(document, TraceEntityKindEnum.Deployment, deployment.Id, "deployment");

            List<ArmadaEvent> events = await ReadEntityEventsAsync(TraceEntityKindEnum.Mission, mission.Id, token).ConfigureAwait(false);
            document.Timeline = BuildTimeline(events);

            ArmadaEvent? gateEvent = events.LastOrDefault(evt => String.Equals(evt.EventType, DefinitionOfDoneEvaluationRecord.EventType, StringComparison.Ordinal));
            if (gateEvent != null) document.Gate = ReadGate(gateEvent.Payload);

            foreach (ArmadaEvent challenge in events.Where(evt => String.Equals(evt.EventType, JudgeChallenge.DispatchedEvent, StringComparison.Ordinal)))
            {
                JudgeChallengeDispatchRecord dispatched = JudgeChallenge.ReadDispatchRecord(challenge.Payload);
                AddLink(document, TraceEntityKindEnum.Mission, dispatched.ChallengerMissionId, "challenger");
            }
            return document;
        }

        private async Task<TraceDocument?> BuildVoyageAsync(string id, CancellationToken token)
        {
            Voyage? voyage = await _Database.Voyages.ReadAsync(id, token).ConfigureAwait(false);
            if (voyage == null) return null;

            TraceDocument document = new TraceDocument
            {
                Id = voyage.Id,
                Kind = TraceEntityKindEnum.Voyage,
                Title = Bound(voyage.Title, TraceDocument.MaximumTitleLength),
                Status = voyage.Status.ToString(),
                TenantId = voyage.TenantId,
                CreatedUtc = voyage.CreatedUtc,
                CompletedUtc = voyage.CompletedUtc,
                LastUpdateUtc = voyage.LastUpdateUtc
            };

            List<MissionSummary> missions = await _Database.Missions.EnumerateMissionSummariesByVoyageAsync(voyage.Id, token).ConfigureAwait(false);
            foreach (MissionSummary mission in missions.Take(_ChildLimit))
            {
                AddLink(document, TraceEntityKindEnum.Mission, mission.Id, "mission");
                if (String.IsNullOrWhiteSpace(document.VesselId)) document.VesselId = mission.VesselId;
            }

            EnumerationResult<CheckRun> checks = await _Database.CheckRuns.EnumerateAsync(
                new CheckRunQuery { VoyageId = voyage.Id, PageNumber = 1, PageSize = _ChildLimit, IncludeOutput = false }, token).ConfigureAwait(false);
            foreach (CheckRun check in checks.Objects) AddLink(document, TraceEntityKindEnum.CheckRun, check.Id, "check");

            EnumerationResult<Deployment> deployments = await _Database.Deployments.EnumerateAsync(
                new DeploymentQuery { VoyageId = voyage.Id, PageNumber = 1, PageSize = _ChildLimit }, token).ConfigureAwait(false);
            foreach (Deployment deployment in deployments.Objects) AddLink(document, TraceEntityKindEnum.Deployment, deployment.Id, "deployment");

            document.Timeline = BuildTimeline(await ReadEntityEventsAsync(TraceEntityKindEnum.Voyage, voyage.Id, token).ConfigureAwait(false));
            return document;
        }

        private async Task<TraceDocument?> BuildObjectiveAsync(string id, CancellationToken token)
        {
            Objective? objective = await _Database.Objectives.ReadAsync(id, token).ConfigureAwait(false);
            if (objective == null) return null;

            TraceDocument document = new TraceDocument
            {
                Id = objective.Id,
                Kind = TraceEntityKindEnum.Objective,
                Title = Bound(objective.Title, TraceDocument.MaximumTitleLength),
                Status = objective.Status.ToString(),
                TenantId = objective.TenantId,
                CreatedUtc = objective.CreatedUtc,
                CompletedUtc = objective.CompletedUtc,
                LastUpdateUtc = objective.LastUpdateUtc,
                VesselId = objective.VesselIds.FirstOrDefault(),
                StartRef = objective.StartFromRef
            };

            AddLink(document, TraceEntityKindEnum.Objective, objective.ParentObjectiveId, "parent");
            foreach (string voyageId in objective.VoyageIds) AddLink(document, TraceEntityKindEnum.Voyage, voyageId, "voyage");
            foreach (string missionId in objective.MissionIds.Take(_ChildLimit)) AddLink(document, TraceEntityKindEnum.Mission, missionId, "mission");
            foreach (string checkRunId in objective.CheckRunIds.Take(_ChildLimit)) AddLink(document, TraceEntityKindEnum.CheckRun, checkRunId, "check");
            foreach (string deploymentId in objective.DeploymentIds) AddLink(document, TraceEntityKindEnum.Deployment, deploymentId, "deployment");
            foreach (string incidentId in objective.IncidentIds) AddLink(document, TraceEntityKindEnum.Incident, incidentId, "incident");

            document.Timeline = BuildTimeline(await ReadEntityEventsAsync(TraceEntityKindEnum.Objective, objective.Id, token).ConfigureAwait(false));
            return document;
        }

        private async Task<TraceDocument?> BuildCheckRunAsync(string id, CancellationToken token)
        {
            CheckRun? run = await _Database.CheckRuns.ReadAsync(id, null, token).ConfigureAwait(false);
            if (run == null) return null;

            TraceDocument document = new TraceDocument
            {
                Id = run.Id,
                Kind = TraceEntityKindEnum.CheckRun,
                Title = Bound(run.Label ?? run.Type.ToString(), TraceDocument.MaximumTitleLength),
                Status = run.Status.ToString(),
                TenantId = run.TenantId,
                CreatedUtc = run.CreatedUtc,
                StartedUtc = run.StartedUtc,
                CompletedUtc = run.CompletedUtc,
                LastUpdateUtc = run.LastUpdateUtc,
                VesselId = run.VesselId,
                BranchName = run.BranchName,
                CommitSha = run.CommitHash,
                LandedSha = run.RegressionLandedCommit,
                Check = new TraceCheckResult
                {
                    Type = run.Type.ToString(),
                    ExitCode = run.ExitCode,
                    Total = run.TestSummary?.Total,
                    Passed = run.TestSummary?.Passed,
                    Failed = run.TestSummary?.Failed,
                    Skipped = run.TestSummary?.Skipped,
                    Summary = Bound(run.Summary, TraceDocument.MaximumReasonLength)
                }
            };

            AddLink(document, TraceEntityKindEnum.Mission, run.MissionId, "mission");
            AddLink(document, TraceEntityKindEnum.Voyage, run.VoyageId, "voyage");
            AddLink(document, TraceEntityKindEnum.Deployment, run.DeploymentId, "deployment");
            AddLink(document, TraceEntityKindEnum.Objective, run.RegressionObjectiveId, "regression_objective");

            document.Timeline = BuildTimeline(await ReadEntityEventsAsync(TraceEntityKindEnum.CheckRun, run.Id, token).ConfigureAwait(false));
            return document;
        }

        private async Task<TraceDocument?> BuildIncidentAsync(string id, CancellationToken token)
        {
            Incident? incident = await _Incidents.ReadAsync(SystemAuth(), id, token).ConfigureAwait(false);
            if (incident == null) return null;

            TraceDocument document = new TraceDocument
            {
                Id = incident.Id,
                Kind = TraceEntityKindEnum.Incident,
                Title = Bound(incident.Title, TraceDocument.MaximumTitleLength),
                Status = incident.Status.ToString() + "/" + incident.Severity.ToString(),
                TenantId = incident.TenantId,
                CreatedUtc = incident.DetectedUtc,
                CompletedUtc = incident.ClosedUtc,
                LastUpdateUtc = incident.LastUpdateUtc,
                VesselId = incident.VesselId,
                LandedSha = incident.RegressionLandedCommit,
                FailureReason = Bound(incident.RootCause ?? incident.Summary, TraceDocument.MaximumReasonLength)
            };

            AddLink(document, TraceEntityKindEnum.Mission, incident.MissionId, "mission");
            AddLink(document, TraceEntityKindEnum.Voyage, incident.VoyageId, "voyage");
            AddLink(document, TraceEntityKindEnum.CheckRun, incident.CheckRunId, "check");
            AddLink(document, TraceEntityKindEnum.Deployment, incident.DeploymentId, "deployment");
            AddLink(document, TraceEntityKindEnum.Deployment, incident.RollbackDeploymentId, "rollback");
            AddLink(document, TraceEntityKindEnum.Objective, incident.RegressionObjectiveId, "regression_objective");

            document.Timeline = BuildTimeline(await ReadEntityEventsAsync(TraceEntityKindEnum.Incident, incident.Id, token).ConfigureAwait(false));
            return document;
        }

        private async Task<TraceDocument?> BuildMergeEntryAsync(string id, CancellationToken token)
        {
            MergeEntry? entry = await _Database.MergeEntries.ReadAsync(id, token).ConfigureAwait(false);
            if (entry == null) return null;

            TraceDocument document = new TraceDocument
            {
                Id = entry.Id,
                Kind = TraceEntityKindEnum.MergeEntry,
                Title = Bound(entry.BranchName + " -> " + entry.TargetBranch, TraceDocument.MaximumTitleLength),
                Status = entry.Status.ToString(),
                TenantId = entry.TenantId,
                CreatedUtc = entry.CreatedUtc,
                StartedUtc = entry.TestStartedUtc,
                CompletedUtc = entry.CompletedUtc,
                LastUpdateUtc = entry.LastUpdateUtc,
                VesselId = entry.VesselId,
                BranchName = entry.BranchName,
                FailureReason = Bound(entry.MergeFailureSummary, TraceDocument.MaximumReasonLength)
            };
            if (entry.MergeFailureClass.HasValue)
                document.FailureReason = entry.MergeFailureClass.Value.ToString() + (document.FailureReason == null ? String.Empty : ": " + document.FailureReason);
            if (entry.TestExitCode.HasValue)
                document.Check = new TraceCheckResult { Type = "MergeTest", ExitCode = entry.TestExitCode };

            if (!String.IsNullOrWhiteSpace(entry.MissionId))
            {
                AddLink(document, TraceEntityKindEnum.Mission, entry.MissionId, "mission");
                Mission? mission = await _Database.Missions.ReadSummaryAsync(entry.MissionId!, token).ConfigureAwait(false);
                if (mission != null)
                {
                    document.CommitSha = mission.CommitHash;
                    document.CaptainId = mission.CaptainId;
                    if (entry.Status == MergeStatusEnum.Landed) document.LandedSha = mission.CommitHash;
                }
            }

            document.Timeline = BuildTimeline(await ReadEntityEventsAsync(TraceEntityKindEnum.MergeEntry, entry.Id, token).ConfigureAwait(false));
            return document;
        }

        private async Task<TraceDocument?> BuildDeploymentAsync(string id, CancellationToken token)
        {
            Deployment? deployment = await _Database.Deployments.ReadAsync(id, null, token).ConfigureAwait(false);
            if (deployment == null) return null;

            TraceDocument document = new TraceDocument
            {
                Id = deployment.Id,
                Kind = TraceEntityKindEnum.Deployment,
                Title = Bound(deployment.Title, TraceDocument.MaximumTitleLength),
                Status = deployment.Status.ToString() + "/" + deployment.VerificationStatus.ToString(),
                TenantId = deployment.TenantId,
                CreatedUtc = deployment.CreatedUtc,
                StartedUtc = deployment.StartedUtc,
                CompletedUtc = deployment.CompletedUtc,
                LastUpdateUtc = deployment.LastUpdateUtc,
                VesselId = deployment.VesselId,
                StartRef = deployment.SourceRef,
                CommitSha = deployment.SourceCommit,
                ArtifactIdentity = deployment.ArtifactIdentity,
                FailureReason = Bound(deployment.Summary, TraceDocument.MaximumReasonLength)
            };

            AddLink(document, TraceEntityKindEnum.Mission, deployment.MissionId, "mission");
            AddLink(document, TraceEntityKindEnum.Voyage, deployment.VoyageId, "voyage");
            List<string> checkRunIds = new List<string>();
            checkRunIds.AddRange(deployment.CheckRunIds);
            checkRunIds.AddRange(deployment.ValidationCheckRunIds);
            checkRunIds.Add(deployment.DeployCheckRunId ?? String.Empty);
            checkRunIds.Add(deployment.SmokeTestCheckRunId ?? String.Empty);
            checkRunIds.Add(deployment.HealthCheckRunId ?? String.Empty);
            checkRunIds.Add(deployment.DeploymentVerificationCheckRunId ?? String.Empty);
            checkRunIds.Add(deployment.RollbackCheckRunId ?? String.Empty);
            checkRunIds.Add(deployment.RollbackVerificationCheckRunId ?? String.Empty);
            foreach (string checkRunId in checkRunIds.Take(_ChildLimit)) AddLink(document, TraceEntityKindEnum.CheckRun, checkRunId, "check");

            document.Timeline = BuildTimeline(await ReadEntityEventsAsync(TraceEntityKindEnum.Deployment, deployment.Id, token).ConfigureAwait(false));
            return document;
        }

        private async Task<List<ArmadaEvent>> ReadEntityEventsAsync(TraceEntityKindEnum kind, string id, CancellationToken token)
        {
            string? entityType = EntityTypeName(kind);
            if (entityType == null) return new List<ArmadaEvent>();
            List<ArmadaEvent> events = await _Database.Events.EnumerateByEntityAsync(entityType, id, _EntityEventLimit, token).ConfigureAwait(false);
            return events.OrderBy(evt => evt.CreatedUtc).ToList();
        }

        private static List<TraceTimelineEntry> BuildTimeline(List<ArmadaEvent> ascending)
        {
            List<TraceTimelineEntry> timeline = ascending
                .Where(evt => IsTimelineEvent(evt.EventType))
                .Select(evt => new TraceTimelineEntry { Event = evt.EventType, Utc = evt.CreatedUtc })
                .ToList();
            if (timeline.Count > TraceDocument.MaximumTimelineEntries)
                timeline = timeline.Skip(timeline.Count - TraceDocument.MaximumTimelineEntries).ToList();
            return timeline;
        }

        private static TraceGateResult? ReadGate(string? payload)
        {
            if (String.IsNullOrWhiteSpace(payload)) return null;
            DefinitionOfDoneEvaluationRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<DefinitionOfDoneEvaluationRecord>(payload);
            }
            catch (JsonException)
            {
                return new TraceGateResult { Outcome = "Unreadable" };
            }
            if (record == null) return null;

            List<string> failed = (record.FailedTestNames ?? new List<string>())
                .Where(name => !String.IsNullOrWhiteSpace(name))
                .Select(name => Bound(name, 300)!)
                .ToList();
            return new TraceGateResult
            {
                Outcome = record.Outcome.ToString(),
                FailureClass = record.FailureClass?.ToString(),
                ExitCode = record.ExitCode,
                CommitSha = record.CommitHash,
                FailedTests = failed.Take(TraceDocument.MaximumFailedTests).ToList(),
                FailedTestsOverflow = record.FailedTestNamesOverflow || failed.Count > TraceDocument.MaximumFailedTests
            };
        }

        private static void AddLink(TraceDocument document, TraceEntityKindEnum kind, string? id, string relation)
        {
            if (String.IsNullOrWhiteSpace(id)) return;
            string trimmed = id.Trim();
            if (String.Equals(trimmed, document.Id, StringComparison.Ordinal)) return;
            foreach (TraceLink existing in document.Links)
            {
                if (String.Equals(existing.Id, trimmed, StringComparison.Ordinal) && String.Equals(existing.Relation, relation, StringComparison.Ordinal)) return;
            }
            document.Links.Add(new TraceLink { Kind = kind, Id = trimmed, Relation = relation });
        }

        private static string? Bound(string? text, int maximumLength)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            string redacted = SecretRedactor.Redact(text.Trim());
            return redacted.Length <= maximumLength ? redacted : redacted.Substring(0, maximumLength);
        }

        private static string? Tail(string? text, int maximumLength)
        {
            if (String.IsNullOrWhiteSpace(text)) return null;
            string redacted = SecretRedactor.Redact(text.Trim());
            return redacted.Length <= maximumLength ? redacted : redacted.Substring(redacted.Length - maximumLength);
        }

        private static AuthContext SystemAuth()
        {
            return AuthContext.Authenticated(
                Constants.DefaultTenantId,
                Constants.DefaultUserId,
                isAdmin: true,
                isTenantAdmin: true,
                authMethod: "System",
                principalDisplay: "Trace recorder");
        }

        #endregion
    }
}
