namespace Armada.Core.Database.Sqlite.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class CaptainMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryReleaseHarborAttemptAsync(HarborJobSnapshot job, Captain captain, CancellationToken token = default)
        {
            using (SqliteConnection connection = new SqliteConnection(_Driver.ConnectionString))
            {
                return await HarborCaptainReleasePersistence.TryReleaseAsync(connection, DatabaseTypeEnum.Sqlite, job, captain, token).ConfigureAwait(false);
            }
        }
    }
}
