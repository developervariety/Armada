namespace Armada.Core.Database.Sqlite.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;

    public partial class MissionMethods : IMissionMethods
    {
        /// <inheritdoc />
        public async Task<int?> TryConsumeContextRecallAsync(string tenantId, string userId, string missionId, int maximum, CancellationToken token = default)
        {
            using (SqliteConnection connection = new SqliteConnection(_Driver.ConnectionString))
                return await MissionContextRecallPersistence.TryConsumeAsync(connection, DatabaseTypeEnum.Sqlite,
                    tenantId, userId, missionId, maximum, token).ConfigureAwait(false);
        }
    }
}

