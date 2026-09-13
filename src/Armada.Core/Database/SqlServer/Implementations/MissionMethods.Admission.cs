namespace Armada.Core.Database.SqlServer.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    internal partial class MissionMethods
    {
        /// <inheritdoc />
        public async Task<bool> TryRecordAdmissionAsync(Mission expected, MissionAdmissionObservation observation,
            CancellationToken token = default)
        {
            using (SqlConnection connection = new SqlConnection(_Driver.ConnectionString))
            {
                return await MissionAdmissionPersistence.RecordAsync(connection, DatabaseTypeEnum.SqlServer,
                    expected, observation, token).ConfigureAwait(false);
            }
        }
    }
}
