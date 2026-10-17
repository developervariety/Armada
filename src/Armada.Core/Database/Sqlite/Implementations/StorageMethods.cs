namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Database.Sqlite.Queries;
    using Armada.Core.Storage;

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
                StorageDatabase result = await ReadAsync(connection, "Sqlite", StorageQueries.Tables, StorageQueries.Size, exactCounts, "\"", "\"", token).ConfigureAwait(false);

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

                result.MeasuredUtc = DateTime.UtcNow;
                return result;
            }
        }
    }
}
