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
        public async Task<VoyageMissionSummary> ReadVoyageMissionSummaryAsync(string voyageId, int pageNumber = 1,
            int pageSize = 100, string? tenantId = null, string? userId = null, CancellationToken token = default)
        {
            using (SqlConnection connection = new SqlConnection(_Driver.ConnectionString))
            {
                return await VoyageMissionSummaryQuery.ReadAsync(connection, DatabaseTypeEnum.SqlServer, voyageId,
                    pageNumber, pageSize, tenantId, userId, token).ConfigureAwait(false);
            }
        }
    }
}
