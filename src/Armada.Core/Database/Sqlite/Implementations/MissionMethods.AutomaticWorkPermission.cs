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
        public async Task<bool> TryWriteAutomaticWorkPermissionAsync(Mission expected, MissionAutomaticWorkPermission next,
            ArmadaEvent audit, CancellationToken token = default)
        {
            using (SqliteConnection connection = new SqliteConnection(_Driver.ConnectionString))
                return await MissionAutomaticWorkPermissionPersistence.TryWriteAsync(connection, DatabaseTypeEnum.Sqlite,
                    expected, next, audit, token).ConfigureAwait(false);
        }
    }
}
