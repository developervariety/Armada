namespace Armada.Core.Database.SqlServer.Implementations
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Database.SqlServer.Queries;
    using Armada.Core.Storage;

    /// <summary>Read-only SqlServer storage statistics, using separate short-lived connections.</summary>
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
            using (SqlConnection connection = new SqlConnection(_ConnectionString))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                StorageDatabase result = await ReadAsync(connection, "SqlServer", StorageQueries.Tables, StorageQueries.Size, exactCounts, "[", "]", token).ConfigureAwait(false);

                result.MeasuredUtc = DateTime.UtcNow;
                return result;
            }
        }
    }
}
