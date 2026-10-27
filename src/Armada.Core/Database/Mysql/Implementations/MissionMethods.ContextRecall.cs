namespace Armada.Core.Database.Mysql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;

    public partial class MissionMethods : IMissionMethods
    {
        /// <inheritdoc />
        public async Task<int?> TryConsumeContextRecallAsync(string tenantId, string userId, string missionId, int maximum, CancellationToken token = default)
        {
            using (MySqlConnection connection = new MySqlConnection(_ConnectionString))
                return await MissionContextRecallPersistence.TryConsumeAsync(connection, DatabaseTypeEnum.Mysql,
                    tenantId, userId, missionId, maximum, token).ConfigureAwait(false);
        }
    }
}

