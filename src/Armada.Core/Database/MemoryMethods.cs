namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
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

        #endregion

        #region Private-Members

        private readonly StoredMethods<Memory> _Rows;

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

        #endregion

        #region Private-Methods

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
