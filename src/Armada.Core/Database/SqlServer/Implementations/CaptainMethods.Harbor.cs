namespace Armada.Core.Database.SqlServer.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class CaptainMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryReleaseHarborAttemptAsync(HarborJobSnapshot job, Captain captain, CancellationToken token = default)
        {
            using (SqlConnection connection = new SqlConnection(_Driver.ConnectionString))
            {
                return await HarborCaptainReleasePersistence.TryReleaseAsync(connection, DatabaseTypeEnum.SqlServer, job, captain, token).ConfigureAwait(false);
            }
        }
    }
}
