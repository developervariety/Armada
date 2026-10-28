namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Microsoft.Data.Sqlite;
    using MySqlConnector;
    using Npgsql;
    using Armada.Core.Enums;
    using Armada.Core.Settings;

    /// <summary>
    /// Reads database size and per-table metadata with read-only statistics queries on SQLite,
    /// PostgreSQL, MySQL and SQL Server. It opens its own short-lived connection, never writes, and bounds
    /// every command with a timeout.
    /// </summary>
    public class DatabaseStorageMeasurer
    {
        #region Private-Members

        private const int _CommandTimeoutSeconds = 5;
        private const int _MaxTables = 2000;

        private const string _SqliteTables = "SELECT 'main', name, NULL, NULL, NULL FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        private const string _PostgresTables = "SELECT schemaname, relname, n_live_tup, pg_table_size(relid), pg_indexes_size(relid) FROM pg_stat_user_tables ORDER BY schemaname, relname";
        private const string _PostgresSize = "SELECT pg_database_size(current_database())";
        private const string _MysqlTables = "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_ROWS, DATA_LENGTH, INDEX_LENGTH FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME";
        private const string _MysqlSize = "SELECT SUM(DATA_LENGTH+INDEX_LENGTH) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE()";
        private const string _SqlServerTables = "SELECT s.name, t.name, SUM(CASE WHEN p.index_id IN (0,1) THEN p.rows ELSE 0 END), SUM(CASE WHEN p.index_id IN (0,1) THEN a.pages ELSE 0 END)*8192, SUM(CASE WHEN p.index_id>1 THEN a.pages ELSE 0 END)*8192 FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id JOIN sys.partitions p ON p.object_id=t.object_id JOIN (SELECT container_id, SUM(total_pages) AS pages FROM sys.allocation_units GROUP BY container_id) a ON a.container_id=p.hobt_id WHERE t.is_ms_shipped=0 GROUP BY s.name, t.name ORDER BY s.name, t.name";
        private const string _SqlServerSize = "SELECT SUM(CONVERT(bigint,size))*8192 FROM sys.database_files";

        private readonly DatabaseSettings _Database;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="database">Configured database connection settings.</param>
        public DatabaseStorageMeasurer(DatabaseSettings database)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        #endregion

        #region Public-Methods

        /// <summary>Measure the configured database.</summary>
        /// <param name="exactCounts">When true, count the rows of every table with a bounded <c>COUNT(*)</c>.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Database size and table metadata; a statistic that cannot be read is named in the warnings.</returns>
        public async Task<StorageDatabase> MeasureAsync(bool exactCounts, CancellationToken token = default)
        {
            switch (_Database.Type)
            {
                case DatabaseTypeEnum.Sqlite:
                    return await MeasureSqliteAsync(exactCounts, token).ConfigureAwait(false);
                case DatabaseTypeEnum.Postgresql:
                    using (NpgsqlConnection connection = new NpgsqlConnection(_Database.GetConnectionString()))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        return await ReadAsync(connection, "Postgresql", _PostgresTables, _PostgresSize, exactCounts, '"', '"', token).ConfigureAwait(false);
                    }
                case DatabaseTypeEnum.Mysql:
                    using (MySqlConnection connection = new MySqlConnection(_Database.GetConnectionString()))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        return await ReadAsync(connection, "Mysql", _MysqlTables, _MysqlSize, exactCounts, '`', '`', token).ConfigureAwait(false);
                    }
                case DatabaseTypeEnum.SqlServer:
                    using (SqlConnection connection = new SqlConnection(_Database.GetConnectionString()))
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        return await ReadAsync(connection, "SqlServer", _SqlServerTables, _SqlServerSize, exactCounts, '[', ']', token).ConfigureAwait(false);
                    }
                default:
                    throw new NotSupportedException("Unknown database type: " + _Database.Type);
            }
        }

        #endregion

        #region Private-Methods

        private async Task<StorageDatabase> MeasureSqliteAsync(bool exactCounts, CancellationToken token)
        {
            SqliteConnectionStringBuilder builder = new SqliteConnectionStringBuilder(_Database.GetConnectionString())
            {
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };
            using (SqliteConnection connection = new SqliteConnection(builder.ConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                StorageDatabase result = await ReadAsync(connection, "Sqlite", _SqliteTables, "PRAGMA page_count", exactCounts, '"', '"', token).ConfigureAwait(false);
                long pageSize = await ScalarAsync(connection, "PRAGMA page_size", token).ConfigureAwait(false) ?? 0;
                if (result.AllocatedBytes.HasValue) result.AllocatedBytes = result.AllocatedBytes.Value * pageSize;
                long? free = await ScalarAsync(connection, "PRAGMA freelist_count", token).ConfigureAwait(false);
                if (free.HasValue) result.UnusedBytes = free.Value * pageSize;
                // The bundled SQLite has no per-table page statistics, so tables list rows only.
                AddWarning(result, StorageWarningCodes.TableSizesUnavailable);
                return result;
            }
        }

        private async Task<StorageDatabase> ReadAsync(DbConnection connection, string provider, string tablesSql, string sizeSql, bool exactCounts, char openQuote, char closeQuote, CancellationToken token)
        {
            StorageDatabase result = new StorageDatabase { Provider = provider, Status = StorageStatusEnum.Complete };
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandTimeout = _CommandTimeoutSeconds;
                command.CommandText = tablesSql;
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (result.Tables.Count >= _MaxTables)
                        {
                            result.Status = StorageStatusEnum.Partial;
                            AddWarning(result, StorageWarningCodes.TableLimit);
                            break;
                        }
                        result.Tables.Add(new StorageTable
                        {
                            Schema = reader.GetString(0),
                            Name = reader.GetString(1),
                            Rows = Number(reader, 2),
                            CountKind = reader.IsDBNull(2) ? StorageCountKindEnum.Unavailable : StorageCountKindEnum.Estimated,
                            DataBytes = Number(reader, 3),
                            IndexBytes = Number(reader, 4)
                        });
                    }
                }
            }
            try
            {
                result.AllocatedBytes = await ScalarAsync(connection, sizeSql, token).ConfigureAwait(false);
            }
            catch (DbException)
            {
                result.Status = StorageStatusEnum.Partial;
                AddWarning(result, StorageWarningCodes.AllocationUnavailable);
            }
            if (exactCounts)
            {
                foreach (StorageTable table in result.Tables)
                {
                    token.ThrowIfCancellationRequested();
                    string quoted = Quote(table.Name, openQuote, closeQuote);
                    if (table.Schema.Length > 0 && provider != "Sqlite") quoted = Quote(table.Schema, openQuote, closeQuote) + "." + quoted;
                    try
                    {
                        string function = provider == "SqlServer" ? "COUNT_BIG" : "COUNT";
                        table.Rows = await ScalarAsync(connection, "SELECT " + function + "(*) FROM " + quoted, token).ConfigureAwait(false);
                        table.CountKind = StorageCountKindEnum.Exact;
                    }
                    catch (DbException)
                    {
                        table.Warning = StorageWarningCodes.ExactCountUnavailable;
                        result.Status = StorageStatusEnum.Partial;
                    }
                }
            }
            result.MeasuredUtc = DateTime.UtcNow;
            return result;
        }

        private static async Task<long?> ScalarAsync(DbConnection connection, string sql, CancellationToken token)
        {
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandTimeout = _CommandTimeoutSeconds;
                command.CommandText = sql;
                object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return value == null || value == DBNull.Value ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
        }

        private static long? Number(DbDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static string Quote(string identifier, char open, char close)
        {
            return open + identifier.Replace(close.ToString(), close.ToString() + close) + close;
        }

        private static void AddWarning(StorageDatabase database, string code)
        {
            if (!database.Warnings.Contains(code)) database.Warnings.Add(code);
        }

        #endregion
    }
}
