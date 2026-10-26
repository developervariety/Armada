namespace Armada.Core.Database.SqlServer.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    internal partial class MissionMethods : IMissionMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryUpdateIntegrationBarrierAsync(Mission expected, MissionIntegrationBarrier next,
            CancellationToken token = default)
        {
            using (SqlConnection connection = new SqlConnection(_Driver.ConnectionString))
                return await MissionIntegrationBarrierPersistence.TryUpdateAsync(connection, DatabaseTypeEnum.SqlServer,
                    expected, next, token).ConfigureAwait(false);
        }
    }
}
