namespace Armada.Core.Database.Mysql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionMethods
    {
        /// <inheritdoc />
        public async Task<VoyageMissionSummary> ReadVoyageMissionSummaryAsync(string voyageId, int pageNumber = 1,
            int pageSize = 100, string? tenantId = null, string? userId = null, CancellationToken token = default)
        {
            using (MySqlConnection connection = new MySqlConnection(_ConnectionString))
            {
                return await VoyageMissionSummaryQuery.ReadAsync(connection, DatabaseTypeEnum.Mysql, voyageId,
                    pageNumber, pageSize, tenantId, userId, token).ConfigureAwait(false);
            }
        }
    }
}
