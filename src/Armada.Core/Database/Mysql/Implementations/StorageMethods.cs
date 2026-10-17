namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Database.Mysql.Queries;
    using Armada.Core.Storage;
    using MySqlConnector;

    /// <summary>Read-only Mysql storage statistics, using separate short-lived connections.</summary>
    public class StorageMethods : StorageMethodsBase, IStorageMethods
    {
        private readonly string _ConnectionString;

        /// <summary>Construct with the active driver's connection string.</summary>
        /// <exception cref="ArgumentNullException">Connection string is null.</exception>
        public StorageMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <inheritdoc />
        public async Task<StorageDatabase> MeasureAsync(bool exactCounts = false, CancellationToken token = default)
        {
            using (MySqlConnection connection = new MySqlConnection(_ConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                StorageDatabase result = await ReadAsync(connection, "Mysql", StorageQueries.Tables, StorageQueries.Size, exactCounts, "`", "`", token).ConfigureAwait(false);

                result.MeasuredUtc = DateTime.UtcNow;
                return result;
            }
        }
    }
}
