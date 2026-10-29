namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Memory persistence, one implementation for every provider. A memory and its tags are written together in one
    /// transaction, and an update applies only while the stored version still matches the expected one.
    /// </summary>
    internal sealed class MemoryMethods : IMemoryMethods
    {
        #region Internal-Members

        /// <summary>
        /// The memories table.
        /// </summary>
        internal static readonly StoredTable<Memory> Table = new StoredTable<Memory>(
            "memories",
            new[]
            {
                "id", "tenant_id", "user_id", "scope", "type", "topic", "memory_key", "summary", "content", "salience", "version", "source_kind",
                "source_voyage_id", "source_mission_id", "source_vessel_id", "source_detail", "vessel_id", "created_utc", "last_update_utc"
            },
            Array.Empty<string>(),
            MemoryColumns.Read,
            MemoryColumns.Write);

        /// <summary>
        /// Newest first.
        /// </summary>
        internal const string Order = "created_utc DESC";

        /// <summary>
        /// Order of a paged read: salience descending, then newest first, then identifier so a page boundary never
        /// moves between requests.
        /// </summary>
        internal const string PageOrder = "salience DESC, created_utc DESC, id ASC";

        #endregion

        #region Private-Members

        private readonly StoredMethods<Memory> _Rows;

        private const string _VesselCondition = "(vessel_id = @vessel_id OR source_vessel_id = @vessel_id "
            + "OR ((vessel_id IS NULL OR vessel_id = '') AND (source_vessel_id IS NULL OR source_vessel_id = '')))";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="dialect">Provider the statements run on.</param>
        internal MemoryMethods(StoredDialect dialect)
        {
            _Rows = new StoredMethods<Memory>(dialect, Table);
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<Memory> CreateAsync(Memory memory, CancellationToken token = default)
        {
            if (memory == null) throw new ArgumentNullException(nameof(memory));

            using (DbConnection connection = await _Rows.Dialect.OpenAsync(token).ConfigureAwait(false))
            using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
            using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                await _Rows.InsertAsync(memory, connection, transaction, token).ConfigureAwait(false);
                await WriteTagsAsync(connection, transaction, memory, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
            }

            return memory;
        }

        /// <inheritdoc />
        public async Task<Memory?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            Memory? memory = await _Rows.FirstAsync(Table.Filter().Key("id", id), token).ConfigureAwait(false);
            return await WithTagsAsync(memory, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Memory?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            Memory? memory = await _Rows.FirstAsync(Table.Filter().Key("tenant_id", tenantId).Key("id", id), token).ConfigureAwait(false);
            return await WithTagsAsync(memory, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Memory?> ReadByKeyAsync(string tenantId, string key, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));
            Memory? memory = await _Rows.FirstAsync(Table.Filter().Key("tenant_id", tenantId).Key("memory_key", key), token).ConfigureAwait(false);
            return await WithTagsAsync(memory, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<bool> UpdateAsync(Memory memory, int expectedVersion, CancellationToken token = default)
        {
            if (memory == null) throw new ArgumentNullException(nameof(memory));
            if (String.IsNullOrEmpty(memory.TenantId)) throw new ArgumentException("A memory update requires the owning tenant.", nameof(memory));

            // MySQL reports changed rows rather than matched rows, which is the same count here because every
            // guarded update advances the version.
            using (DbConnection connection = await _Rows.Dialect.OpenAsync(token).ConfigureAwait(false))
            using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
            using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                int rows;
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = MemoryRows.UpdateSql;
                    MemoryColumns.Write(_Rows.Dialect.Binder.For(command, Table.Name), memory);
                    StoredValueBinder.Value(command, "@expected_version", expectedVersion);
                    rows = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                if (rows != 1)
                {
                    await transaction.RollbackAsync(token).ConfigureAwait(false);
                    return false;
                }

                await WriteTagsAsync(connection, transaction, memory, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            }
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            // Tags go first, and only for a memory the tenant owns, so the delete holds on a store whose tag rows
            // reference their memory without a cascade.
            using (DbConnection connection = await _Rows.Dialect.OpenAsync(token).ConfigureAwait(false))
            using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
            using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM memory_tags WHERE memory_id = @id AND EXISTS (SELECT 1 FROM memories WHERE id = @id AND tenant_id = @tenant_id);";
                    StoredValueBinder.Value(command, "@tenant_id", tenantId);
                    StoredValueBinder.Value(command, "@id", id);
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM context_embeddings WHERE tenant_id = @tenant_id AND source_key = @source_key;";
                    StoredValueBinder.Value(command, "@tenant_id", tenantId);
                    StoredValueBinder.Value(command, "@source_key", ContextEmbedding.MemorySourceKey(id));
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                int rows;
                using (DbCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM memories WHERE tenant_id = @tenant_id AND id = @id;";
                    StoredValueBinder.Value(command, "@tenant_id", tenantId);
                    StoredValueBinder.Value(command, "@id", id);
                    rows = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return rows == 1;
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<Memory>> EnumeratePageAsync(MemoryEnumerateRequest request, string? tenantId, string? viewerUserId, CancellationToken token = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            EnumerationResult<Memory> page = await _Rows.PageAsync(
                Page(Table.Filter(), request, tenantId, viewerUserId, _Rows.Dialect.Provider), PageOrder, request.PageNumber, request.PageSize, token).ConfigureAwait(false);
            if (page.Objects.Count > 0)
            {
                List<string> names = new List<string>();
                for (int i = 0; i < page.Objects.Count; i++) names.Add("@memory_" + i);
                List<string> ids = page.Objects.ConvertAll(memory => memory.Id);
                await WithTagsAsync(
                    page.Objects,
                    "SELECT memory_id, tag FROM memory_tags WHERE memory_id IN (" + String.Join(", ", names) + ");",
                    command =>
                    {
                        for (int i = 0; i < ids.Count; i++) StoredValueBinder.Value(command, names[i], ids[i]);
                    },
                    token).ConfigureAwait(false);
            }

            return EnumerationResult<Memory>.Create(request, page.Objects, page.TotalRecords);
        }

        /// <inheritdoc />
        public async Task<List<Memory>> EnumerateAsync(CancellationToken token = default)
        {
            List<Memory> memories = await _Rows.ListAsync(Table.Filter(), Order, token).ConfigureAwait(false);
            if (memories.Count == 0) return memories;
            return await WithTagsAsync(memories, "SELECT memory_id, tag FROM memory_tags;", null, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<Memory>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            List<Memory> memories = await _Rows.ListAsync(Table.Filter().Key("tenant_id", tenantId), Order, token).ConfigureAwait(false);
            if (memories.Count == 0) return memories;
            return await WithTagsAsync(
                memories,
                "SELECT t.memory_id, t.tag FROM memory_tags t INNER JOIN memories m ON m.id = t.memory_id WHERE m.tenant_id = @tenant_id;",
                command => StoredValueBinder.Value(command, "@tenant_id", tenantId),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<Memory>> EnumerateForVesselAsync(string tenantId, string vesselId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            List<Memory> memories = await _Rows.ListAsync(
                Table.Filter().Key("tenant_id", tenantId).Condition(_VesselCondition, p => p.Text("@vessel_id", "vessel_id", vesselId)),
                Order, token).ConfigureAwait(false);
            if (memories.Count == 0) return memories;
            return await WithTagsAsync(
                memories,
                "SELECT t.memory_id, t.tag FROM memory_tags t INNER JOIN memories m ON m.id = t.memory_id WHERE m.tenant_id = @tenant_id "
                    + "AND (m.vessel_id = @vessel_id OR m.source_vessel_id = @vessel_id "
                    + "OR ((m.vessel_id IS NULL OR m.vessel_id = '') AND (m.source_vessel_id IS NULL OR m.source_vessel_id = '')));",
                command =>
                {
                    StoredValueBinder.Value(command, "@tenant_id", tenantId);
                    StoredValueBinder.Value(command, "@vessel_id", vesselId);
                },
                token).ConfigureAwait(false);
        }

        #endregion

        #region Internal-Methods

        /// <summary>
        /// Add the tenant, viewer, type, topic, vessel, voyage, mission, creation-time and search conditions of a
        /// paged read. A record is visible to a viewer when it is tenant-wide or belongs to that viewer, the same
        /// rule as the ownership policy applies to one record.
        /// </summary>
        internal static StoredFilter Page(StoredFilter filter, MemoryEnumerateRequest request, string? tenantId, string? viewerUserId, DatabaseTypeEnum provider)
        {
            filter.Text("tenant_id", tenantId);
            if (!String.IsNullOrWhiteSpace(viewerUserId))
            {
                // A record stored without a user belongs to the default user.
                string ownedByDefault = String.Equals(viewerUserId, Constants.DefaultUserId, StringComparison.Ordinal)
                    ? " OR user_id IS NULL OR user_id = ''"
                    : String.Empty;
                filter.Condition("(scope IS NULL OR scope <> '" + MemoryScopeEnum.UserSpecific + "' OR user_id = @viewer_user_id" + ownedByDefault + ")",
                    p => p.Text("@viewer_user_id", "user_id", viewerUserId));
            }

            filter.Name("type", request.Type);
            if (!String.IsNullOrWhiteSpace(request.Topic))
            {
                string topic = request.Topic.Trim().ToLowerInvariant();
                filter.Condition("LOWER(topic) = @topic", p => p.Text("@topic", "topic", topic));
            }
            if (!String.IsNullOrWhiteSpace(request.VesselId))
            {
                string vesselId = request.VesselId;
                filter.Condition("(vessel_id = @vessel_id OR source_vessel_id = @vessel_id)", p => p.Text("@vessel_id", "vessel_id", vesselId));
            }
            filter
                .Text("source_voyage_id", "@voyage_id", request.VoyageId)
                .Text("source_mission_id", "@mission_id", request.MissionId)
                .Time("created_utc", ">", "@created_after", request.CreatedAfter)
                .Time("created_utc", "<", "@created_before", request.CreatedBefore);

            if (!String.IsNullOrWhiteSpace(request.Search))
            {
                // The needle is literal text: LIKE wildcards in it are escaped. PostgreSQL matches
                // case-insensitively with ILIKE; the other providers lower both sides.
                string needle = request.Search.Trim();
                bool postgresql = provider == DatabaseTypeEnum.Postgresql;
                string pattern = "%" + EscapeLike(postgresql ? needle : needle.ToLowerInvariant(), provider) + "%";
                string like = postgresql ? " ILIKE @search ESCAPE '!'" : " LIKE @search ESCAPE '!'";
                string Column(string name) => postgresql ? name : "LOWER(" + name + ")";
                filter.Condition("(" + Column("content") + like + " OR " + Column("summary") + like + " OR " + Column("topic") + like
                    + " OR " + Column("memory_key") + like
                    + " OR EXISTS (SELECT 1 FROM memory_tags t WHERE t.memory_id = memories.id AND " + Column("t.tag") + like + "))",
                    p => p.Text("@search", "content", pattern));
            }

            return filter;
        }

        #endregion

        #region Private-Methods

        // '!' is the escape character of every search condition. SQL Server also reads '[' as a wildcard.
        private static string EscapeLike(string text, DatabaseTypeEnum provider)
        {
            string escaped = text.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
            return provider == DatabaseTypeEnum.SqlServer ? escaped.Replace("[", "![") : escaped;
        }

        private async Task<Memory?> WithTagsAsync(Memory? memory, CancellationToken token)
        {
            if (memory == null) return null;
            string id = memory.Id;
            List<Memory> tagged = await WithTagsAsync(
                new List<Memory> { memory },
                "SELECT memory_id, tag FROM memory_tags WHERE memory_id = @id;",
                command => StoredValueBinder.Value(command, "@id", id),
                token).ConfigureAwait(false);
            return tagged[0];
        }

        private async Task<List<Memory>> WithTagsAsync(List<Memory> memories, string tagSql, Action<DbCommand>? parameterize, CancellationToken token)
        {
            Dictionary<string, List<string>> tags = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            using (DbConnection connection = await _Rows.Dialect.OpenAsync(token).ConfigureAwait(false))
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = tagSql;
                parameterize?.Invoke(command);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        string memoryId = reader["memory_id"].ToString()!;
                        if (!tags.TryGetValue(memoryId, out List<string>? list))
                        {
                            list = new List<string>();
                            tags[memoryId] = list;
                        }
                        list.Add(reader["tag"].ToString()!);
                    }
                }
            }

            MemoryRows.AttachTags(memories, tags);
            return memories;
        }

        private static async Task WriteTagsAsync(DbConnection connection, DbTransaction transaction, Memory memory, CancellationToken token)
        {
            using (DbCommand delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM memory_tags WHERE memory_id = @memory_id;";
                StoredValueBinder.Value(delete, "@memory_id", memory.Id);
                await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            foreach (string tag in MemoryRows.TagsToWrite(memory))
            {
                using (DbCommand insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO memory_tags (memory_id, tag) VALUES (@memory_id, @tag);";
                    StoredValueBinder.Value(insert, "@memory_id", memory.Id);
                    StoredValueBinder.Value(insert, "@tag", tag);
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        #endregion
    }
}
