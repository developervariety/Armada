namespace Armada.Core.Database.SqlServer.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;

    internal partial class MissionMethods : IMissionMethods
    {
        /// <inheritdoc />
        public async Task<int?> TryConsumeContextRecallAsync(string tenantId, string userId, string missionId, int maximum, CancellationToken token = default)
        {
            using (SqlConnection connection = new SqlConnection(_Driver.ConnectionString))
                return await MissionContextRecallPersistence.TryConsumeAsync(connection, DatabaseTypeEnum.SqlServer,
                    tenantId, userId, missionId, maximum, token).ConfigureAwait(false);
        }
    }
}

