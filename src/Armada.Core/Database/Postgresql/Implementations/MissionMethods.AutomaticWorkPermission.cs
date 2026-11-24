namespace Armada.Core.Database.Postgresql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionMethods : IMissionMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryWriteAutomaticWorkPermissionAsync(Mission expected, MissionAutomaticWorkPermission next,
            ArmadaEvent audit, CancellationToken token = default)
        {
            using (NpgsqlConnection connection = new NpgsqlConnection(_Driver.ConnectionString))
                return await MissionAutomaticWorkPermissionPersistence.TryWriteAsync(connection, DatabaseTypeEnum.Postgresql,
                    expected, next, audit, token).ConfigureAwait(false);
        }
    }
}
