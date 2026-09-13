namespace Armada.Core.Database.Postgresql.Implementations
{
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    public partial class MissionMethods
    {
        /// <inheritdoc />
        public async Task<VoyageMissionSummary> ReadVoyageMissionSummaryAsync(string voyageId, int pageNumber = 1,
            int pageSize = 100, string? tenantId = null, string? userId = null, CancellationToken token = default)
        {
            using (NpgsqlConnection connection = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                return await VoyageMissionSummaryQuery.ReadAsync(connection, DatabaseTypeEnum.Postgresql, voyageId,
                    pageNumber, pageSize, tenantId, userId, token).ConfigureAwait(false);
            }
        }
    }
}
