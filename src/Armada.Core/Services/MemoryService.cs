namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Diagnostics;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Creates, reads, updates, deletes and searches native captain memory records.
    ///
    /// Every call is scoped to the caller: a caller sees the tenant-wide records of its own tenant plus
    /// its own user-specific records, and never a record of another tenant. A tenant or global
    /// administrator may also change tenant-wide records. Search and paging run over the caller-visible
    /// set, which suits the modest volume of distilled records.
    ///
    /// The service is self-contained and reads no global enable setting.
    /// </summary>
    public class MemoryService
    {
        #region Public-Members

        /// <summary>
        /// Longest accepted content. A record is a distilled finding, not a log.
        /// </summary>
        public const int MaximumContentLength = 65536;

        /// <summary>
        /// Longest accepted summary, topic and key.
        /// </summary>
        public const int MaximumShortFieldLength = 255;

        /// <summary>
        /// Longest accepted tag.
        /// </summary>
        public const int MaximumTagLength = 128;

        /// <summary>
        /// Most tags on one record.
        /// </summary>
        public const int MaximumTagCount = 32;

        #endregion

        #region Private-Members

        private readonly string _Header = "[MemoryService] ";
        private readonly DatabaseDriver _Database;
        private readonly LoggingModule? _Logging;

        // A key and a tag are identifiers, not prose. One lowercase slug shape keeps a record
        // addressable by the same string on every provider, whose collations differ in case and
        // accent sensitivity.
        private static readonly Regex _KeyPattern = new Regex(@"^[a-z0-9][a-z0-9._:/+-]*$", RegexOptions.Compiled);
        private static readonly Regex _TagPattern = new Regex(@"^[a-z0-9][a-z0-9._:/+#-]*$", RegexOptions.Compiled);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Optional logging module.</param>
        public MemoryService(DatabaseDriver database, LoggingModule? logging = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Enumerate the records visible to the caller, ordered by salience and then by recency.
        /// </summary>
        /// <param name="auth">Authentication context.</param>
        /// <param name="query">Paging, vessel, voyage, mission and date filters.</param>
        /// <param name="search">Optional case-insensitive substring over content, summary, topic, key and tags.</param>
        /// <param name="type">Optional type filter.</param>
        /// <param name="topic">Optional exact topic filter.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>One page of records.</returns>
        public async Task<EnumerationResult<Memory>> EnumerateAsync(
            AuthContext auth,
            EnumerationQuery query,
            string? search = null,
            MemoryTypeEnum? type = null,
            string? topic = null,
            CancellationToken token = default)
        {
            RequireCaller(auth);
            if (query == null) query = new EnumerationQuery();

            Stopwatch sw = Stopwatch.StartNew();
            List<Memory> visible = await LoadVisibleAsync(auth, token).ConfigureAwait(false);

            IEnumerable<Memory> filtered = visible;
            if (type.HasValue) filtered = filtered.Where(memory => memory.Type == type.Value);
            if (!String.IsNullOrWhiteSpace(topic)) filtered = filtered.Where(memory => String.Equals(memory.Topic, topic.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!String.IsNullOrWhiteSpace(query.VesselId))
                filtered = filtered.Where(memory => String.Equals(memory.VesselId, query.VesselId, StringComparison.Ordinal)
                    || String.Equals(memory.SourceVesselId, query.VesselId, StringComparison.Ordinal));
            if (!String.IsNullOrWhiteSpace(query.VoyageId)) filtered = filtered.Where(memory => String.Equals(memory.SourceVoyageId, query.VoyageId, StringComparison.Ordinal));
            if (!String.IsNullOrWhiteSpace(query.MissionId)) filtered = filtered.Where(memory => String.Equals(memory.SourceMissionId, query.MissionId, StringComparison.Ordinal));
            if (query.CreatedAfter.HasValue) filtered = filtered.Where(memory => memory.CreatedUtc > query.CreatedAfter.Value);
            if (query.CreatedBefore.HasValue) filtered = filtered.Where(memory => memory.CreatedUtc < query.CreatedBefore.Value);

            if (!String.IsNullOrWhiteSpace(search))
            {
                string needle = search.Trim();
                filtered = filtered.Where(memory =>
                    Contains(memory.Content, needle)
                    || Contains(memory.Summary, needle)
                    || Contains(memory.Topic, needle)
                    || Contains(memory.Key, needle)
                    || memory.Tags.Any(tag => Contains(tag, needle)));
            }

            List<Memory> ordered = filtered
                .OrderByDescending(memory => memory.Salience)
                .ThenByDescending(memory => memory.CreatedUtc)
                .ThenBy(memory => memory.Id, StringComparer.Ordinal)
                .ToList();

            List<Memory> page = ordered
                .Skip((query.PageNumber - 1) * query.PageSize)
                .Take(query.PageSize)
                .ToList();

            EnumerationResult<Memory> result = EnumerationResult<Memory>.Create(query, page, ordered.Count);
            sw.Stop();
            result.TotalMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2);
            return result;
        }

        /// <summary>
        /// Read one record the caller may see, or null.
        /// </summary>
        /// <param name="auth">Authentication context.</param>
        /// <param name="id">Record identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The record, or null when it is absent or not visible.</returns>
        public async Task<Memory?> ReadAsync(AuthContext auth, string id, CancellationToken token = default)
        {
            RequireCaller(auth);
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            Memory? memory = await _Database.Memories.ReadAsync(id.Trim(), token).ConfigureAwait(false);
            if (memory == null || !CanView(auth, memory)) return null;
            return memory;
        }

        /// <summary>
        /// Create a record owned by the caller. A regular user always creates a user-specific record;
        /// a tenant or global administrator may create a tenant-wide one.
        /// </summary>
        /// <param name="auth">Authentication context.</param>
        /// <param name="memory">Record to create.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created record.</returns>
        public async Task<Memory> CreateAsync(AuthContext auth, Memory memory, CancellationToken token = default)
        {
            RequireCaller(auth);
            if (memory == null) throw new ArgumentNullException(nameof(memory));

            Normalize(memory);
            memory.TenantId = TenantOf(auth);
            memory.UserId = UserOf(auth);
            if (!IsAdministrator(auth)) memory.Scope = MemoryScopeEnum.UserSpecific;
            memory.Version = 1;
            memory.CreatedUtc = DateTime.UtcNow;
            memory.LastUpdateUtc = DateTime.UtcNow;

            if (!String.IsNullOrEmpty(memory.Key))
            {
                Memory? taken = await _Database.Memories.ReadByKeyAsync(memory.TenantId!, memory.Key!, token).ConfigureAwait(false);
                if (taken != null) throw new MemoryConflictException("key", "A memory with key '" + memory.Key + "' already exists in this tenant.");
            }

            try
            {
                Memory created = await _Database.Memories.CreateAsync(memory, token).ConfigureAwait(false);
                _Logging?.Info(_Header + "created " + created.Type + " memory " + created.Id);
                return created;
            }
            catch (DbException)
            {
                // The storage enforces one key per tenant, so a racing writer surfaces here.
                if (String.IsNullOrEmpty(memory.Key)) throw;
                Memory? taken = await _Database.Memories.ReadByKeyAsync(memory.TenantId!, memory.Key!, token).ConfigureAwait(false);
                if (taken == null) throw;
                throw new MemoryConflictException("key", "A memory with key '" + memory.Key + "' already exists in this tenant.");
            }
        }

        /// <summary>
        /// Create a record, or update the record that already carries the same key in the caller's
        /// tenant. This is how a captain records the same finding twice without making a duplicate.
        /// A record without a key is always created.
        /// </summary>
        /// <param name="auth">Authentication context.</param>
        /// <param name="memory">Record to write.</param>
        /// <param name="expectedVersion">Version the caller read, when it wants a conflict instead of an overwrite.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The written record.</returns>
        public async Task<Memory> UpsertAsync(AuthContext auth, Memory memory, int? expectedVersion = null, CancellationToken token = default)
        {
            RequireCaller(auth);
            if (memory == null) throw new ArgumentNullException(nameof(memory));

            Normalize(memory);
            if (String.IsNullOrEmpty(memory.Key))
            {
                if (expectedVersion.HasValue) throw new ArgumentException("A memory without a key is always created, so it has no version to match.");
                return await CreateAsync(auth, memory, token).ConfigureAwait(false);
            }

            string tenantId = TenantOf(auth);
            Memory? existing = await _Database.Memories.ReadByKeyAsync(tenantId, memory.Key!, token).ConfigureAwait(false);
            if (existing == null)
            {
                try
                {
                    return await CreateAsync(auth, memory, token).ConfigureAwait(false);
                }
                catch (MemoryConflictException)
                {
                    existing = await _Database.Memories.ReadByKeyAsync(tenantId, memory.Key!, token).ConfigureAwait(false);
                    if (existing == null) throw;
                }
            }

            if (!CanEdit(auth, existing)) throw new MemoryConflictException("key", "Key '" + memory.Key + "' belongs to a memory you may not modify.");
            if (expectedVersion.HasValue && expectedVersion.Value != existing.Version)
                throw new MemoryConflictException("version", "Memory " + existing.Id + " is at version " + existing.Version + ", not " + expectedVersion.Value + ".");

            int readVersion = existing.Version;
            existing.Type = memory.Type;
            existing.Topic = memory.Topic;
            existing.Summary = memory.Summary;
            existing.Content = memory.Content;
            existing.Salience = memory.Salience;
            existing.SourceKind = memory.SourceKind;
            existing.SourceVoyageId = memory.SourceVoyageId;
            existing.SourceMissionId = memory.SourceMissionId;
            existing.SourceVesselId = memory.SourceVesselId;
            existing.SourceDetail = memory.SourceDetail;
            existing.VesselId = memory.VesselId;
            existing.Tags = memory.Tags;
            if (IsAdministrator(auth)) existing.Scope = memory.Scope;
            existing.Version = readVersion + 1;
            existing.LastUpdateUtc = DateTime.UtcNow;

            if (!await _Database.Memories.UpdateAsync(existing, readVersion, token).ConfigureAwait(false))
                throw new MemoryConflictException("version", "Memory " + existing.Id + " changed while it was being written. Read it again and retry.");

            _Logging?.Info(_Header + "updated memory " + existing.Id + " through its key");
            return existing;
        }

        /// <summary>
        /// Apply a partial change to one record. Fields the caller did not supply keep their value.
        /// </summary>
        /// <param name="auth">Authentication context.</param>
        /// <param name="id">Record identifier.</param>
        /// <param name="update">Fields to change.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The updated record.</returns>
        public async Task<Memory> UpdateAsync(AuthContext auth, string id, MemoryUpdate update, CancellationToken token = default)
        {
            RequireCaller(auth);
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            if (update == null) throw new ArgumentNullException(nameof(update));

            Memory? existing = await _Database.Memories.ReadAsync(id.Trim(), token).ConfigureAwait(false);
            if (existing == null || !CanView(auth, existing)) throw new KeyNotFoundException("Memory not found: " + id);
            if (!CanEdit(auth, existing)) throw new UnauthorizedAccessException("You may not modify memory " + id + ".");
            if (update.ExpectedVersion.HasValue && update.ExpectedVersion.Value != existing.Version)
                throw new MemoryConflictException("version", "Memory " + existing.Id + " is at version " + existing.Version + ", not " + update.ExpectedVersion.Value + ".");

            int readVersion = existing.Version;
            if (update.Type != null) existing.Type = ParseRequired<MemoryTypeEnum>(update.Type, "type");
            if (update.Topic != null) existing.Topic = EmptyToNull(update.Topic);
            if (update.Summary != null) existing.Summary = EmptyToNull(update.Summary);
            if (update.Content != null) existing.Content = update.Content;
            if (update.Salience.HasValue) existing.Salience = update.Salience.Value;
            if (update.Tags != null) existing.Tags = update.Tags;
            if (update.VesselId != null) existing.VesselId = EmptyToNull(update.VesselId);
            if (update.SourceDetail != null) existing.SourceDetail = EmptyToNull(update.SourceDetail);
            if (update.Scope != null)
            {
                if (!IsAdministrator(auth)) throw new UnauthorizedAccessException("Only a tenant administrator may change a memory's scope.");
                existing.Scope = ParseRequired<MemoryScopeEnum>(update.Scope, "scope");
            }

            string? previousKey = existing.Key;
            if (update.Key != null) existing.Key = EmptyToNull(update.Key);
            Normalize(existing);

            if (!String.IsNullOrEmpty(existing.Key) && !String.Equals(existing.Key, previousKey, StringComparison.Ordinal))
            {
                Memory? taken = await _Database.Memories.ReadByKeyAsync(existing.TenantId!, existing.Key!, token).ConfigureAwait(false);
                if (taken != null && !String.Equals(taken.Id, existing.Id, StringComparison.Ordinal))
                    throw new MemoryConflictException("key", "A memory with key '" + existing.Key + "' already exists in this tenant.");
            }

            existing.Version = readVersion + 1;
            existing.LastUpdateUtc = DateTime.UtcNow;

            bool applied;
            try
            {
                applied = await _Database.Memories.UpdateAsync(existing, readVersion, token).ConfigureAwait(false);
            }
            catch (DbException)
            {
                if (String.IsNullOrEmpty(existing.Key)) throw;
                throw new MemoryConflictException("key", "A memory with key '" + existing.Key + "' already exists in this tenant.");
            }

            if (!applied) throw new MemoryConflictException("version", "Memory " + existing.Id + " changed while it was being written. Read it again and retry.");

            _Logging?.Info(_Header + "updated memory " + existing.Id);
            return existing;
        }

        /// <summary>
        /// Delete one record the caller may change, for example a record that went stale.
        /// </summary>
        /// <param name="auth">Authentication context.</param>
        /// <param name="id">Record identifier.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task DeleteAsync(AuthContext auth, string id, CancellationToken token = default)
        {
            RequireCaller(auth);
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            Memory? existing = await _Database.Memories.ReadAsync(id.Trim(), token).ConfigureAwait(false);
            if (existing == null || !CanView(auth, existing)) throw new KeyNotFoundException("Memory not found: " + id);
            if (!CanEdit(auth, existing)) throw new UnauthorizedAccessException("You may not delete memory " + id + ".");

            if (!await _Database.Memories.DeleteAsync(existing.TenantId!, existing.Id, token).ConfigureAwait(false))
                throw new KeyNotFoundException("Memory not found: " + id);

            _Logging?.Info(_Header + "deleted memory " + existing.Id);
        }

        #endregion

        #region Private-Methods

        private async Task<List<Memory>> LoadVisibleAsync(AuthContext auth, CancellationToken token)
        {
            if (auth.IsAdmin) return await _Database.Memories.EnumerateAsync(token).ConfigureAwait(false);

            List<Memory> tenantRecords = await _Database.Memories.EnumerateAsync(TenantOf(auth), token).ConfigureAwait(false);
            if (auth.IsTenantAdmin) return tenantRecords;
            return tenantRecords.Where(memory => CanView(auth, memory)).ToList();
        }

        private static void RequireCaller(AuthContext auth)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (!auth.IsAuthenticated) throw new UnauthorizedAccessException("Authentication is required to use native memory.");
        }

        private static string TenantOf(AuthContext auth)
        {
            return String.IsNullOrWhiteSpace(auth.TenantId) ? Constants.DefaultTenantId : auth.TenantId!;
        }

        private static string UserOf(AuthContext auth)
        {
            return String.IsNullOrWhiteSpace(auth.UserId) ? Constants.DefaultUserId : auth.UserId!;
        }

        private static bool IsAdministrator(AuthContext auth)
        {
            return auth.IsAdmin || auth.IsTenantAdmin;
        }

        private static bool SameTenant(AuthContext auth, Memory memory)
        {
            return String.Equals(memory.TenantId, TenantOf(auth), StringComparison.Ordinal);
        }

        private static bool CanView(AuthContext auth, Memory memory)
        {
            if (auth.IsAdmin) return true;
            if (!SameTenant(auth, memory)) return false;
            if (auth.IsTenantAdmin) return true;
            if (memory.Scope == MemoryScopeEnum.TenantWide) return true;
            return String.Equals(memory.UserId, UserOf(auth), StringComparison.Ordinal);
        }

        private static bool CanEdit(AuthContext auth, Memory memory)
        {
            if (auth.IsAdmin) return true;
            if (!SameTenant(auth, memory)) return false;
            if (auth.IsTenantAdmin) return true;
            if (memory.Scope != MemoryScopeEnum.UserSpecific) return false;
            return String.Equals(memory.UserId, UserOf(auth), StringComparison.Ordinal);
        }

        private static bool Contains(string? haystack, string needle)
        {
            return !String.IsNullOrEmpty(haystack) && haystack!.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string? EmptyToNull(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static TEnum ParseRequired<TEnum>(string value, string field) where TEnum : struct
        {
            if (!Enum.TryParse<TEnum>(value.Trim(), true, out TEnum parsed))
                throw new ArgumentException("Unknown " + field + ": '" + value + "'.");
            return parsed;
        }

        private static void Normalize(Memory memory)
        {
            if (String.IsNullOrWhiteSpace(memory.Content)) throw new ArgumentException("Memory content is required.");
            if (memory.Content.Length > MaximumContentLength)
                throw new ArgumentException("Memory content is longer than " + MaximumContentLength + " characters. Record the finding, not the log.");

            memory.Topic = EmptyToNull(memory.Topic ?? String.Empty);
            if (memory.Topic != null && memory.Topic.Length > MaximumShortFieldLength)
                throw new ArgumentException("Memory topic is longer than " + MaximumShortFieldLength + " characters.");

            memory.Summary = EmptyToNull(memory.Summary ?? String.Empty);
            if (memory.Summary != null && memory.Summary.Length > MaximumShortFieldLength)
                throw new ArgumentException("Memory summary is longer than " + MaximumShortFieldLength + " characters.");

            string? key = EmptyToNull(memory.Key ?? String.Empty);
            if (key != null)
            {
                key = key.ToLowerInvariant();
                if (key.Length > MaximumShortFieldLength) throw new ArgumentException("Memory key is longer than " + MaximumShortFieldLength + " characters.");
                if (!_KeyPattern.IsMatch(key))
                    throw new ArgumentException("Memory key '" + key + "' is not a slug. Use lowercase letters, digits and . _ : / + -, starting with a letter or digit.");
            }
            memory.Key = key;

            List<string> tags = new List<string>();
            foreach (string raw in memory.Tags)
            {
                if (String.IsNullOrWhiteSpace(raw)) continue;
                string tag = raw.Trim().ToLowerInvariant();
                if (tag.Length > MaximumTagLength) throw new ArgumentException("Memory tag '" + tag + "' is longer than " + MaximumTagLength + " characters.");
                if (!_TagPattern.IsMatch(tag))
                    throw new ArgumentException("Memory tag '" + tag + "' is not a slug. Use lowercase letters, digits and . _ : / + # -, starting with a letter or digit.");
                if (!tags.Contains(tag, StringComparer.Ordinal)) tags.Add(tag);
            }
            if (tags.Count > MaximumTagCount) throw new ArgumentException("A memory carries at most " + MaximumTagCount + " tags.");
            memory.Tags = tags;

            memory.VesselId = EmptyToNull(memory.VesselId ?? String.Empty);
            memory.SourceVoyageId = EmptyToNull(memory.SourceVoyageId ?? String.Empty);
            memory.SourceMissionId = EmptyToNull(memory.SourceMissionId ?? String.Empty);
            memory.SourceVesselId = EmptyToNull(memory.SourceVesselId ?? String.Empty);
            memory.SourceDetail = EmptyToNull(memory.SourceDetail ?? String.Empty);
        }

        #endregion
    }
}
