namespace Armada.Core.Database
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>
    /// Refuses startup when the live schema stores a column in a form the provider's stored value binder does not
    /// write. After migrations every timestamp column must hold the binder's form for it (a zone-less timestamp type,
    /// PostgreSQL TIMESTAMPTZ, or text), every column the binder names must exist, and every boolean the binder
    /// stores as an integer must be an integer column. Backup tables are copies the code never writes and are not
    /// checked. The refusal lists every mismatch, so one start names the whole drift.
    /// </summary>
    internal static class StoredBinderSchemaGuard
    {
        #region Internal-Methods

        /// <summary>
        /// Compare the binder with the live schema and throw on any mismatch.
        /// </summary>
        /// <param name="connection">Open connection to the migrated database.</param>
        /// <param name="provider">Provider of the connection.</param>
        /// <param name="token">Cancellation token.</param>
        internal static async Task EnsureAsync(DbConnection connection, DatabaseTypeEnum provider, CancellationToken token)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            Dictionary<string, string> columns = await ReadColumnTypesAsync(connection, provider, token).ConfigureAwait(false);
            List<string> mismatches = Mismatches(StoredValueBinder.For(provider), provider, columns);
            if (mismatches.Count > 0)
                throw new InvalidOperationException("Incompatible schema prerequisite stored column forms: " + String.Join("; ", mismatches)
                    + "; existing data and migration history were not replaced");
        }

        /// <summary>
        /// Every way the live column types differ from the binder's stored forms, one entry per column.
        /// </summary>
        /// <param name="binder">Provider binder.</param>
        /// <param name="provider">Provider.</param>
        /// <param name="columns">Live column types keyed "table.column".</param>
        /// <returns>The mismatches; empty when the schema matches.</returns>
        internal static List<string> Mismatches(StoredValueBinder binder, DatabaseTypeEnum provider, IReadOnlyDictionary<string, string> columns)
        {
            List<string> mismatches = new List<string>();
            foreach (KeyValuePair<string, string> column in columns)
            {
                string[] parts = column.Key.Split('.');
                if (IsIgnored(parts[0]) || !parts[1].EndsWith("_utc", StringComparison.OrdinalIgnoreCase)) continue;
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
                    StoredTimestampEnum.ServerRenderedText => text && (provider == DatabaseTypeEnum.Postgresql || provider == DatabaseTypeEnum.Mysql),
                    _ => false
                };
                if (!fits) mismatches.Add(column.Key + " is " + column.Value + " but is written as " + storage);
            }

            foreach (string named in binder.NamedTimestamps.Keys)
            {
                if (!columns.ContainsKey(named)) mismatches.Add(named + " is written but does not exist");
            }

            foreach (string integer in binder.IntegerBooleanColumns)
            {
                if (!columns.TryGetValue(integer, out string? type)) mismatches.Add(integer + " is written but does not exist");
                else if (!type.ToLowerInvariant().Contains("int", StringComparison.Ordinal)) mismatches.Add(integer + " is " + type + " but is written as an integer boolean");
            }

            mismatches.Sort(StringComparer.Ordinal);
            return mismatches;
        }

        #endregion

        #region Private-Methods

        private static bool IsIgnored(string table)
        {
            return table.Contains("_backup_", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<Dictionary<string, string>> ReadColumnTypesAsync(DbConnection connection, DatabaseTypeEnum provider, CancellationToken token)
        {
            Dictionary<string, string> columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (provider == DatabaseTypeEnum.Sqlite)
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
                        command.CommandText = "PRAGMA table_info(\"" + table.Replace("\"", "\"\"") + "\");";
                        using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                            while (await reader.ReadAsync(token).ConfigureAwait(false))
                                columns[table + "." + Convert.ToString(reader["name"], CultureInfo.InvariantCulture)] = Convert.ToString(reader["type"], CultureInfo.InvariantCulture) ?? String.Empty;
                    }
                }

                return columns;
            }

            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = provider switch
                {
                    DatabaseTypeEnum.Postgresql => "SELECT table_name, column_name, data_type FROM information_schema.columns WHERE table_schema = current_schema();",
                    DatabaseTypeEnum.Mysql => "SELECT table_name, column_name, column_type FROM information_schema.columns WHERE table_schema = DATABASE();",
                    DatabaseTypeEnum.SqlServer => "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = SCHEMA_NAME();",
                    _ => throw new NotSupportedException("No stored column guard for " + provider + ".")
                };
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                        columns[reader.GetString(0) + "." + reader.GetString(1)] = reader.GetString(2);
            }

            return columns;
        }

        #endregion
    }
}
