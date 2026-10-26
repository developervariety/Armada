namespace Armada.Core.Database.Sqlite.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionMethods : IMissionMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryUpdateIntegrationBarrierAsync(Mission expected, MissionIntegrationBarrier next,
            CancellationToken token = default)
        {
            using (SqliteConnection connection = new SqliteConnection(_Driver.ConnectionString))
                return await MissionIntegrationBarrierPersistence.TryUpdateAsync(connection, DatabaseTypeEnum.Sqlite,
                    expected, next, token).ConfigureAwait(false);
        }
    }
}
