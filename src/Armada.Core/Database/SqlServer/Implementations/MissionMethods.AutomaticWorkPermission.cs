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
        public async Task<bool> TryWriteAutomaticWorkPermissionAsync(Mission expected, MissionAutomaticWorkPermission next,
            ArmadaEvent audit, CancellationToken token = default)
        {
            using (SqlConnection connection = new SqlConnection(_Driver.ConnectionString))
                return await MissionAutomaticWorkPermissionPersistence.TryWriteAsync(connection, DatabaseTypeEnum.SqlServer,
                    expected, next, audit, token).ConfigureAwait(false);
        }
    }
}
