#nullable enable

namespace Armada.Test.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Npgsql;

    /// <summary>
    /// The provider binder's stored forms against the live schema, and the PostgreSQL plans of the hot filtered
    /// reads under the binder's parameter types.
    /// </summary>
    internal sealed class StoredBinderSchemaTests
    {
        private readonly DatabaseSettings _Settings;

        internal StoredBinderSchemaTests(DatabaseSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Every timestamp column of the migrated schema is bound in a form its stored type holds: a zone-less
        /// timestamp type as a zone-less timestamp, PostgreSQL TIMESTAMPTZ as a zone-aware one, and a text type as
        /// text. Every column the binder names exists, and every boolean it stores as an integer is an integer.
        /// </summary>
        internal async Task VerifyStorageMatchesSchemaAsync(CancellationToken token)
        {
            StoredValueBinder binder = BinderFor(_Settings.Type);
            Dictionary<string, string> columns = await ReadColumnTypesAsync(token).ConfigureAwait(false);
            List<string> problems = new List<string>();
            foreach (KeyValuePair<string, string> column in columns)
            {
                string[] parts = column.Key.Split('.');
                if (!parts[1].EndsWith("_utc", StringComparison.OrdinalIgnoreCase)) continue;
                if (parts[0] == "schema_migrations" || parts[0] == "schema_repairs") continue;
                StoredTimestampEnum storage = binder.TimestampStorage(parts[0], parts[1]);
                string type = column.Value.ToLowerInvariant();
                bool zoned = type == "timestamp with time zone";
                bool zoneless = type == "timestamp without time zone" || type.StartsWith("datetime", StringComparison.Ordinal);
                bool text = !zoned && !zoneless;
                bool fits = storage switch
                {
                    StoredTimestampEnum.TimestampWithZone => zoned,
                    StoredTimestampEnum.Timestamp => zoneless,
                    StoredTimestampEnum.Iso8601Text => text,
                    StoredTimestampEnum.ServerRenderedText => text && (_Settings.Type == DatabaseTypeEnum.Postgresql || _Settings.Type == DatabaseTypeEnum.Mysql),
                    _ => false
                };
                if (!fits) problems.Add(column.Key + " is " + column.Value + " but binds as " + storage);
            }

            foreach (string named in binder.NamedTimestamps.Keys)
            {
                if (!columns.ContainsKey(named)) problems.Add("the binder names " + named + ", which the schema does not have");
            }

            foreach (string integer in binder.IntegerBooleanColumns)
            {
                if (!columns.TryGetValue(integer, out string? type)) problems.Add("the binder names boolean " + integer + ", which the schema does not have");
                else if (!type.ToLowerInvariant().Contains("int")) problems.Add(integer + " is " + type + " but binds as an integer boolean");
            }

            DatabaseAssert.True(problems.Count == 0, String.Join("; ", problems));
        }

        /// <summary>
        /// The hot filtered reads plan the same index whether the filter is bound by driver inference or by the
        /// binder. The plans are printed so a run records them.
        /// </summary>
        internal async Task VerifyPostgresqlHotReadPlansAsync(CancellationToken token)
        {
            if (_Settings.Type != DatabaseTypeEnum.Postgresql) throw new DatabaseTestSkipException("postgresql_only");
            HotRead[] reads = new[]
            {
                new HotRead("missions by status", "missions", "SELECT * FROM missions WHERE status = @status ORDER BY priority ASC, created_utc ASC;", "status", "InProgress", null),
                new HotRead("voyages by status", "voyages", "SELECT * FROM voyages WHERE status = @status ORDER BY created_utc DESC;", "status", "Open", null),
                new HotRead("events by type", "events", "SELECT * FROM events WHERE event_type = @event_type ORDER BY created_utc DESC LIMIT @limit;", "event_type", "mission.completed", 100)
            };
            List<string> problems = new List<string>();
            using (NpgsqlConnection connection = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand off = new NpgsqlCommand("SET enable_seqscan = off;", connection))
                    await off.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                foreach (HotRead read in reads)
                {
                    string inferred = await ExplainAsync(connection, read, false, token).ConfigureAwait(false);
                    string bound = await ExplainAsync(connection, read, true, token).ConfigureAwait(false);
                    Console.WriteLine("PLAN " + read.Label + " (driver inference): " + inferred);
                    Console.WriteLine("PLAN " + read.Label + " (binder): " + bound);
                    string inferredIndex = IndexOf(inferred);
                    string boundIndex = IndexOf(bound);
                    if (inferredIndex.Length == 0) problems.Add(read.Label + " uses no index: " + inferred);
                    if (!String.Equals(inferredIndex, boundIndex, StringComparison.Ordinal))
                        problems.Add(read.Label + " plans " + inferredIndex + " by inference but " + boundIndex + " through the binder");
                }
            }

            DatabaseAssert.True(problems.Count == 0, String.Join("; ", problems));
        }

        private static async Task<string> ExplainAsync(NpgsqlConnection connection, HotRead read, bool throughBinder, CancellationToken token)
        {
            using (NpgsqlCommand command = new NpgsqlCommand("EXPLAIN " + read.Sql, connection))
            {
                if (throughBinder)
                {
                    StoredParameters parameters = PostgresqlBinder().For(command, read.Table).Text(read.Column, read.Value);
                    if (read.Limit.HasValue) parameters.Int("@limit", "limit", read.Limit.Value);
                }
                else
                {
                    command.Parameters.AddWithValue("@" + read.Column, read.Value);
                    if (read.Limit.HasValue) command.Parameters.AddWithValue("@limit", read.Limit.Value);
                }

                List<string> lines = new List<string>();
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false)) lines.Add(reader.GetString(0).Trim());
                }

                return String.Join(" | ", lines);
            }
        }

        private static string IndexOf(string plan)
        {
            const string marker = " using ";
            int start = plan.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return String.Empty;
            start += marker.Length;
            int end = plan.IndexOf(' ', start);
            return end < 0 ? plan.Substring(start) : plan.Substring(start, end - start);
        }

        private async Task<Dictionary<string, string>> ReadColumnTypesAsync(CancellationToken token)
        {
            Dictionary<string, string> columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (DbConnection connection = MigrationScenarioRunner.CreateConnection(_Settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                if (_Settings.Type == DatabaseTypeEnum.Sqlite)
                {
                    List<string> tables = new List<string>();
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
                        using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                            while (await reader.ReadAsync(token).ConfigureAwait(false)) tables.Add(reader.GetString(0));
                    }

                    foreach (string table in tables)
                    {
                        using (DbCommand command = connection.CreateCommand())
                        {
                            command.CommandText = "PRAGMA table_info(\"" + table + "\");";
                            using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                                while (await reader.ReadAsync(token).ConfigureAwait(false))
                                    columns[table + "." + Convert.ToString(reader["name"])] = Convert.ToString(reader["type"]) ?? String.Empty;
                        }
                    }

                    return columns;
                }

                using (DbCommand command = connection.CreateCommand())
                {
                    command.CommandText = _Settings.Type switch
                    {
                        DatabaseTypeEnum.Postgresql => "SELECT table_name, column_name, data_type FROM information_schema.columns WHERE table_schema = current_schema();",
                        DatabaseTypeEnum.Mysql => "SELECT table_name, column_name, column_type FROM information_schema.columns WHERE table_schema = DATABASE();",
                        _ => "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = SCHEMA_NAME();"
                    };
                    using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            columns[reader.GetString(0) + "." + reader.GetString(1)] = reader.GetString(2);
                }
            }

            return columns;
        }

        private static StoredValueBinder BinderFor(DatabaseTypeEnum type)
        {
            return type switch
            {
                DatabaseTypeEnum.Sqlite => Armada.Core.Database.Sqlite.SqliteDatabaseDriver.StoredBinder,
                DatabaseTypeEnum.Postgresql => PostgresqlBinder(),
                DatabaseTypeEnum.Mysql => Armada.Core.Database.Mysql.MysqlDatabaseDriver.StoredBinder,
                DatabaseTypeEnum.SqlServer => Armada.Core.Database.SqlServer.SqlServerDatabaseDriver.StoredBinder,
                _ => throw new NotSupportedException(type.ToString())
            };
        }

        private static StoredValueBinder PostgresqlBinder()
        {
            return Armada.Core.Database.Postgresql.PostgresqlDatabaseDriver.StoredBinder;
        }

        private sealed class HotRead
        {
            internal string Label { get; }
            internal string Table { get; }
            internal string Sql { get; }
            internal string Column { get; }
            internal string Value { get; }
            internal int? Limit { get; }

            internal HotRead(string label, string table, string sql, string column, string value, int? limit)
            {
                Label = label;
                Table = table;
                Sql = sql;
                Column = column;
                Value = value;
                Limit = limit;
            }
        }
    }
}
