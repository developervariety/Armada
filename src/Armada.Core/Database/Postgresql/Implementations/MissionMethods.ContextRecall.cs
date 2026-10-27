namespace Armada.Core.Database.Postgresql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;

    public partial class MissionMethods : IMissionMethods
    {
        /// <inheritdoc />
        public async Task<int?> TryConsumeContextRecallAsync(string tenantId, string userId, string missionId, int maximum, CancellationToken token = default)
        {
            using (NpgsqlConnection connection = new NpgsqlConnection(_Driver.ConnectionString))
                return await MissionContextRecallPersistence.TryConsumeAsync(connection, DatabaseTypeEnum.Postgresql,
                    tenantId, userId, missionId, maximum, token).ConfigureAwait(false);
        }
    }
}

