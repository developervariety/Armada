namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Data.Common;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Database.Sqlite.Queries;
    using Armada.Core.Storage;
    using SQLitePCL;

    /// <summary>Read-only Sqlite storage statistics, using separate short-lived connections.</summary>
    public class StorageMethods : StorageMethodsBase, IStorageMethods
    {
        private readonly string _ConnectionString;

        /// <summary>Construct with the active driver's connection string.</summary>
        /// <exception cref="ArgumentNullException">Connection string is null.</exception>
        public StorageMethods(string connectionString)
        {
            if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));
            SqliteConnectionStringBuilder builder = new SqliteConnectionStringBuilder(connectionString);
            builder.Mode = SqliteOpenMode.ReadOnly;
            _ConnectionString = builder.ConnectionString;
        }

        /// <inheritdoc />
        public async Task<StorageDatabase> MeasureAsync(bool exactCounts = false, CancellationToken token = default)
        {
            using (SqliteConnection connection = new SqliteConnection(_ConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                StorageDatabase result = await ReadAsync(connection, "Sqlite", StorageQueries.Tables, StorageQueries.Size, false, "\"", "\"", token).ConfigureAwait(false);

                long pageSize = await ScalarAsync(connection, "PRAGMA page_size;", token).ConfigureAwait(false) ?? 0;
                result.AllocatedBytes *= pageSize;
                result.UnusedBytes = (await ScalarAsync(connection, "PRAGMA freelist_count;", token).ConfigureAwait(false)) * pageSize;
                try
                {
                    using (DbCommand command = connection.CreateCommand())
                    {
                        command.CommandTimeout = 5;
                        command.CommandText = "SELECT s.tbl_name, s.type, SUM(d.pgsize) FROM dbstat d JOIN sqlite_schema s ON s.name=d.name GROUP BY s.tbl_name,s.type";
                        using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                        {
                            while (await reader.ReadAsync(token).ConfigureAwait(false))
                            {
                                StorageTable? table = result.Tables.Find(t => t.Name == reader.GetString(0));
                                if (table == null) continue;
                                if (reader.GetString(1) == "index") table.IndexBytes = (table.IndexBytes ?? 0) + Convert.ToInt64(reader.GetValue(2));
                                else table.DataBytes = Convert.ToInt64(reader.GetValue(2));
                            }
                        }
                    }
                }
                catch (DbException) { result.Warnings.Add("TableAllocationUnavailable"); result.Status = "Partial"; }

                await MeasureValuesAsync(connection, result, token).ConfigureAwait(false);
                result.MeasuredUtc = DateTime.UtcNow;
                return result;
            }
        }
        private async Task MeasureValuesAsync(SqliteConnection connection, StorageDatabase result, CancellationToken token)
        {
            // CommandTimeout only bounds SQLite lock waits. A VM progress handler also bounds execution.
            Stopwatch queryTime = Stopwatch.StartNew();
            raw.sqlite3_progress_handler(connection.Handle, 1000,
                _ => token.IsCancellationRequested || queryTime.Elapsed.TotalSeconds >= 5 ? 1 : 0, null);
            try
            {
                foreach (StorageTable table in result.Tables)
                {
                    token.ThrowIfCancellationRequested();
                    queryTime.Restart();
                    try
                    {
                        List<string> values = new List<string>();
                        using (SqliteCommand columns = connection.CreateCommand())
                        {
                            columns.CommandTimeout = 5;
                            columns.CommandText = "SELECT name FROM pragma_table_xinfo($table) WHERE hidden != 1";
                            columns.Parameters.AddWithValue("$table", table.Name);
                            using (DbDataReader reader = await columns.ExecuteReaderAsync(token).ConfigureAwait(false))
                            {
                                while (await reader.ReadAsync(token).ConfigureAwait(false))
                                {
                                    string column = Quote(reader.GetString(0));
                                    values.Add("CASE typeof(" + column + ") WHEN 'null' THEN 0 WHEN 'integer' THEN 8 WHEN 'real' THEN 8 ELSE length(CAST(" + column + " AS BLOB)) END");
                                }
                            }
                        }
                        using (SqliteCommand command = connection.CreateCommand())
                        {
                            command.CommandTimeout = 5;
                            command.CommandText = "SELECT COUNT(*), COALESCE(SUM(" + (values.Count == 0 ? "0" : String.Join(" + ", values)) + "), 0) FROM main." + Quote(table.Name);
                            using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                            {
                                if (await reader.ReadAsync(token).ConfigureAwait(false))
                                {
                                    table.Rows = reader.GetInt64(0);
                                    table.CountKind = "Exact";
                                    table.LogicalBytes = reader.GetInt64(1);
                                }
                            }
                        }
                    }
                    catch (SqliteException)
                    {
                        token.ThrowIfCancellationRequested();
                        table.Warning = "TableValuesUnavailable";
                        result.Status = "Partial";
                    }
                }
                token.ThrowIfCancellationRequested();
            }
            finally
            {
                raw.sqlite3_progress_handler(connection.Handle, 0, null, null);
            }
        }

        private static string Quote(string identifier)
        {
            return "\"" + identifier.Replace("\"", "\"\"") + "\"";
        }
    }
}
