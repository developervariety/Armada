namespace Armada.Core.Database.Mysql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionMethods : IMissionMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryUpdateIntegrationBarrierAsync(Mission expected, MissionIntegrationBarrier next,
            CancellationToken token = default)
        {
            using (MySqlConnection connection = new MySqlConnection(_ConnectionString))
                return await MissionIntegrationBarrierPersistence.TryUpdateAsync(connection, DatabaseTypeEnum.Mysql,
                    expected, next, token).ConfigureAwait(false);
        }
    }
}
