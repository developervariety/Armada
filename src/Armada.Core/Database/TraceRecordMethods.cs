namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Json;
    using Armada.Core.Models;

    /// <summary>
    /// Provider-neutral implementation of the trace record store. Only the migration DDL differs per
    /// provider. A write replaces the stored record of the same id and its outgoing links in one
    /// transaction, so writing a record again is safe.
    /// </summary>
    public sealed class TraceRecordMethods : ITraceRecordMethods
    {
        #region Private-Members

        private const string _Columns = "id, kind, tenant_id, status, subject_created_utc, subject_last_update_utc, recorded_utc, document";
        private const int _IdBatchSize = 200;
        private readonly Func<DbConnection> _ConnectionFactory;
        private readonly DatabaseTypeEnum _Provider;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="connectionFactory">Creates an unopened provider connection.</param>
        /// <param name="provider">Database provider.</param>
        public TraceRecordMethods(Func<DbConnection> connectionFactory, DatabaseTypeEnum provider)
        {
            _ConnectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _Provider = provider;
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task UpsertAsync(TraceRecord record, CancellationToken token = default)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            string document = JsonSerializer.Serialize(record.Document);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "DELETE FROM trace_records WHERE id = @id;";
                        ProductionFactSql.Add(command, "@id", record.Id);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "INSERT INTO trace_records (" + _Columns + ") VALUES (@id, @kind, @tenant_id, @status, @subject_created_utc, @subject_last_update_utc, @recorded_utc, @document);";
                        ProductionFactSql.Add(command, "@id", record.Id);
                        ProductionFactSql.Add(command, "@kind", record.Kind.ToString());
                        ProductionFactSql.Add(command, "@tenant_id", record.TenantId);
                        ProductionFactSql.Add(command, "@status", ProductionFactSql.BoundCode(record.Status, 64));
                        ProductionFactSql.AddUtc(command, "@subject_created_utc", record.SubjectCreatedUtc, _Provider);
                        ProductionFactSql.AddUtc(command, "@subject_last_update_utc", record.SubjectLastUpdateUtc, _Provider);
                        ProductionFactSql.AddUtc(command, "@recorded_utc", record.RecordedUtc, _Provider);
                        ProductionFactSql.Add(command, "@document", document);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "DELETE FROM trace_links WHERE source_id = @id;";
                        ProductionFactSql.Add(command, "@id", record.Id);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    foreach (TraceLink link in DistinctLinks(record.Document.Links))
                    {
                        using (DbCommand command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = "INSERT INTO trace_links (source_id, source_kind, target_id, target_kind, relation) VALUES (@source_id, @source_kind, @target_id, @target_kind, @relation);";
                            ProductionFactSql.Add(command, "@source_id", record.Id);
                            ProductionFactSql.Add(command, "@source_kind", record.Kind.ToString());
                            ProductionFactSql.Add(command, "@target_id", link.Id);
                            ProductionFactSql.Add(command, "@target_kind", link.Kind.ToString());
                            ProductionFactSql.Add(command, "@relation", ProductionFactSql.BoundCode(link.Relation, 64) ?? String.Empty);
                            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                        }
                    }
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<TraceRecord?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) return null;
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT " + _Columns + " FROM trace_records WHERE id = @id;";
                    ProductionFactSql.Add(command, "@id", id.Trim());
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false)) return FromReader(reader);
                    }
                }
            }
            return null;
        }

        /// <inheritdoc />
        public async Task<List<string>> EnumerateIdsByPrefixAsync(string prefix, int limit, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(prefix)) throw new ArgumentNullException(nameof(prefix));
            string trimmed = prefix.Trim();
            // The id alphabet holds '_', a LIKE wildcard, so LIKE returns a superset that the ordinal
            // prefix test below narrows to exact matches.
            return await SelectIdsAsync(
                "SELECT id FROM trace_records WHERE id LIKE @prefix ORDER BY id;",
                command => ProductionFactSql.Add(command, "@prefix", trimmed.Replace("%", String.Empty) + "%"),
                id => id.StartsWith(trimmed, StringComparison.Ordinal),
                Math.Max(1, limit),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<string>> EnumerateIdsLastUpdatedBeforeAsync(DateTime cutoffUtc, int limit, CancellationToken token = default)
        {
            return await SelectIdsAsync(
                "SELECT id FROM trace_records WHERE subject_last_update_utc < @cutoff ORDER BY subject_last_update_utc;",
                command => ProductionFactSql.AddUtc(command, "@cutoff", cutoffUtc, _Provider),
                _ => true,
                Math.Max(1, limit),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<int> DeleteAsync(IReadOnlyCollection<string> ids, CancellationToken token = default)
        {
            if (ids == null) throw new ArgumentNullException(nameof(ids));
            List<string> distinct = ids.Where(id => !String.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
            int deleted = 0;
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                for (int start = 0; start < distinct.Count; start += _IdBatchSize)
                {
                    List<string> batch = distinct.Skip(start).Take(_IdBatchSize).ToList();
                    List<string> names = batch.Select((_, index) => "@id" + index.ToString(CultureInfo.InvariantCulture)).ToList();
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "DELETE FROM trace_links WHERE source_id IN (" + String.Join(", ", names) + ");";
                        for (int index = 0; index < batch.Count; index++) ProductionFactSql.Add(command, names[index], batch[index]);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "DELETE FROM trace_records WHERE id IN (" + String.Join(", ", names) + ");";
                        for (int index = 0; index < batch.Count; index++) ProductionFactSql.Add(command, names[index], batch[index]);
                        deleted += await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }
            }
            return deleted;
        }

        /// <inheritdoc />
        public async Task<List<TraceLink>> EnumerateLinksToAsync(string targetId, int limit, CancellationToken token = default)
        {
            List<TraceLink> links = new List<TraceLink>();
            if (String.IsNullOrWhiteSpace(targetId)) return links;
            int bounded = Math.Max(1, limit);
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT source_id, source_kind, relation FROM trace_links WHERE target_id = @target_id ORDER BY source_id;";
                    ProductionFactSql.Add(command, "@target_id", targetId.Trim());
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (links.Count < bounded && await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string kind = Convert.ToString(reader["source_kind"], CultureInfo.InvariantCulture) ?? String.Empty;
                            if (!Enum.TryParse(kind, false, out TraceEntityKindEnum parsedKind)) continue;
                            links.Add(new TraceLink
                            {
                                Kind = parsedKind,
                                Id = Convert.ToString(reader["source_id"], CultureInfo.InvariantCulture)!,
                                Relation = ProductionFactSql.ReadText(reader["relation"]) ?? String.Empty
                            });
                        }
                    }
                }
            }
            return links;
        }

        /// <inheritdoc />
        public async Task<long> CountAsync(CancellationToken token = default)
        {
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM trace_records;";
                    object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                    return value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
                }
            }
        }

        /// <inheritdoc />
        public async Task<string?> ReadCursorAsync(string name, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT value FROM trace_cursors WHERE name = @name;";
                    ProductionFactSql.Add(command, "@name", name);
                    object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                    return value == null || value == DBNull.Value ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
                }
            }
        }

        /// <inheritdoc />
        public async Task WriteCursorAsync(string name, string value, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            if (value == null) throw new ArgumentNullException(nameof(value));
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "DELETE FROM trace_cursors WHERE name = @name;";
                        ProductionFactSql.Add(command, "@name", name);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = "INSERT INTO trace_cursors (name, value, updated_utc) VALUES (@name, @value, @updated_utc);";
                        ProductionFactSql.Add(command, "@name", name);
                        ProductionFactSql.Add(command, "@value", value);
                        ProductionFactSql.AddUtc(command, "@updated_utc", DateTime.UtcNow, _Provider);
                        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                }
            }
        }

        #endregion

        #region Private-Methods

        private async Task<List<string>> SelectIdsAsync(string sql, Action<DbCommand> bind, Func<string, bool> keep, int limit, CancellationToken token)
        {
            List<string> ids = new List<string>();
            using (DbConnection connection = _ConnectionFactory())
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    bind(command);
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (ids.Count < limit && await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string id = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
                            if (keep(id)) ids.Add(id);
                        }
                    }
                }
            }
            return ids;
        }

        private static List<TraceLink> DistinctLinks(List<TraceLink>? links)
        {
            List<TraceLink> distinct = new List<TraceLink>();
            if (links == null) return distinct;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (TraceLink link in links)
            {
                if (link == null || String.IsNullOrWhiteSpace(link.Id)) continue;
                if (seen.Add(link.Id + "|" + link.Relation)) distinct.Add(link);
            }
            return distinct;
        }

        private static TraceRecord FromReader(DbDataReader reader)
        {
            string kind = Convert.ToString(reader["kind"], CultureInfo.InvariantCulture)!;
            if (!Enum.TryParse(kind, false, out TraceEntityKindEnum parsedKind))
                throw new InvalidOperationException("Stored trace record has an unknown kind: " + kind);

            string documentJson = Convert.ToString(reader["document"], CultureInfo.InvariantCulture) ?? "{}";
            TraceDocument document = JsonSerializer.Deserialize<TraceDocument>(documentJson, JsonDefaults.Insensitive) ?? new TraceDocument();
            return new TraceRecord
            {
                Id = Convert.ToString(reader["id"], CultureInfo.InvariantCulture)!,
                Kind = parsedKind,
                TenantId = ProductionFactSql.ReadText(reader["tenant_id"]),
                Status = ProductionFactSql.ReadText(reader["status"]),
                SubjectCreatedUtc = ProductionFactSql.ReadUtc(reader["subject_created_utc"]),
                SubjectLastUpdateUtc = ProductionFactSql.ReadUtc(reader["subject_last_update_utc"]),
                RecordedUtc = ProductionFactSql.ReadUtc(reader["recorded_utc"]),
                Document = document
            };
        }

        #endregion
    }
}
