namespace Armada.Core.Database.Postgresql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    public partial class CaptainMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryReleaseHarborAttemptAsync(HarborJobSnapshot job, Captain captain, CancellationToken token = default)
        {
            using (NpgsqlConnection connection = _Driver.CreateConnection())
            {
                return await HarborCaptainReleasePersistence.TryReleaseAsync(connection, DatabaseTypeEnum.Postgresql, job, captain, token).ConfigureAwait(false);
            }
        }
    }
}
