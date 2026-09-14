namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Settings;
    using Microsoft.Data.SqlClient;
    using Microsoft.Data.Sqlite;
    using MySqlConnector;
    using Npgsql;

    /// <summary>
    /// Counts rows in named tables through the configured provider's own client, so a backup manifest reflects
    /// the database the admiral actually uses.
    /// </summary>
    public static class DatabaseRecordCounter
    {
        /// <summary>
        /// Core tables reported in backup manifests.
        /// </summary>
        public static readonly IReadOnlyList<string> ManifestTables = new[]
        {
            "fleets", "vessels", "captains", "missions", "voyages", "docks", "signals", "events", "merge_entries"
        };

        /// <summary>
        /// Count rows in each table that exists. Absent tables are omitted rather than reported as empty.
        /// </summary>
        /// <param name="settings">Database settings of the configured provider.</param>
        /// <param name="tables">Fixed table names.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Row counts keyed by table name.</returns>
        public static async Task<Dictionary<string, long>> CountAsync(
            DatabaseSettings settings,
            IReadOnlyList<string> tables,
            CancellationToken token = default)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (tables == null) throw new ArgumentNullException(nameof(tables));
            Dictionary<string, long> counts = new Dictionary<string, long>(StringComparer.Ordinal);
            using (DbConnection connection = CreateConnection(settings))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                foreach (string table in tables)
                {
                    if (!IsSafeTableName(table)) throw new ArgumentException("Unsupported table name: " + table, nameof(tables));
                    using (DbCommand exists = connection.CreateCommand())
                    {
                        exists.CommandText = ExistsSql(settings.Type);
                        DbParameter parameter = exists.CreateParameter();
                        parameter.ParameterName = "@table";
                        parameter.Value = table;
                        exists.Parameters.Add(parameter);
                        object? found = await exists.ExecuteScalarAsync(token).ConfigureAwait(false);
                        if (found == null || found == DBNull.Value || Convert.ToInt64(found) == 0) continue;
                    }

                    using (DbCommand count = connection.CreateCommand())
                    {
                        count.CommandText = "SELECT COUNT(*) FROM " + QuoteTable(settings.Type, table);
                        object? value = await count.ExecuteScalarAsync(token).ConfigureAwait(false);
                        counts[table] = value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value);
                    }
                }
            }
            return counts;
        }

        private static DbConnection CreateConnection(DatabaseSettings settings)
        {
            string connectionString = settings.GetConnectionString();
            switch (settings.Type)
            {
                case DatabaseTypeEnum.Sqlite: return new SqliteConnection(connectionString + ";Mode=ReadOnly;Pooling=False");
                case DatabaseTypeEnum.Postgresql: return new NpgsqlConnection(connectionString);
                case DatabaseTypeEnum.Mysql: return new MySqlConnection(connectionString);
                case DatabaseTypeEnum.SqlServer: return new SqlConnection(connectionString);
                default: throw new ArgumentException("Unknown database type: " + settings.Type);
            }
        }

        private static string ExistsSql(DatabaseTypeEnum type)
        {
            switch (type)
            {
                case DatabaseTypeEnum.Sqlite:
                    return "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@table";
                case DatabaseTypeEnum.Postgresql:
                    return "SELECT COUNT(*) FROM information_schema.tables WHERE table_name=@table AND table_schema = current_schema()";
                case DatabaseTypeEnum.Mysql:
                    return "SELECT COUNT(*) FROM information_schema.tables WHERE table_name=@table AND table_schema = DATABASE()";
                default:
                    return "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME=@table AND TABLE_SCHEMA = SCHEMA_NAME()";
            }
        }

        private static string QuoteTable(DatabaseTypeEnum type, string table)
        {
            switch (type)
            {
                case DatabaseTypeEnum.Mysql: return "`" + table + "`";
                case DatabaseTypeEnum.SqlServer: return "[" + table + "]";
                default: return "\"" + table + "\"";
            }
        }

        private static bool IsSafeTableName(string table)
        {
            if (String.IsNullOrEmpty(table)) return false;
            foreach (char character in table)
            {
                if (!(character == '_' || (character >= 'a' && character <= 'z'))) return false;
            }
            return true;
        }
    }
}
