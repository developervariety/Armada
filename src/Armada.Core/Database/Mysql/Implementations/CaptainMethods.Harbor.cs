namespace Armada.Core.Database.Mysql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class CaptainMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryReleaseHarborAttemptAsync(HarborJobSnapshot job, Captain captain, CancellationToken token = default)
        {
            using (MySqlConnection connection = new MySqlConnection(_ConnectionString))
            {
                return await HarborCaptainReleasePersistence.TryReleaseAsync(connection, DatabaseTypeEnum.Mysql, job, captain, token).ConfigureAwait(false);
            }
        }
    }
}
