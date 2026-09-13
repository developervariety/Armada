namespace Armada.Core.Database.Sqlite.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionMethods
    {
        /// <inheritdoc />
        public async Task<VoyageMissionSummary> ReadVoyageMissionSummaryAsync(string voyageId, int pageNumber = 1,
            int pageSize = 100, string? tenantId = null, string? userId = null, CancellationToken token = default)
        {
            using (SqliteConnection connection = new SqliteConnection(_Driver.ConnectionString))
            {
                return await VoyageMissionSummaryQuery.ReadAsync(connection, DatabaseTypeEnum.Sqlite, voyageId,
                    pageNumber, pageSize, tenantId, userId, token).ConfigureAwait(false);
            }
        }
    }
}
