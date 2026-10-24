namespace Armada.Core.Database.SqlServer.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Enums;

    internal partial class MissionMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryBindHarborJobAsync(HarborMissionBindingUpdate update, CancellationToken token = default)
        {
            using (SqlConnection connection = new SqlConnection(_Driver.ConnectionString))
            {
                return await HarborMissionBindingPersistence.TryBindAsync(connection, DatabaseTypeEnum.SqlServer, update, token).ConfigureAwait(false);
            }
        }
    }
}
