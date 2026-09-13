namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Read a bounded aggregate without loading mission payloads.</summary>
    internal static class VoyageMissionSummaryQuery
    {
        internal static async Task<VoyageMissionSummary> ReadAsync(DbConnection connection, DatabaseTypeEnum provider,
            string voyageId, int pageNumber, int pageSize, string? tenantId, string? userId, CancellationToken token)
        {
            if (String.IsNullOrEmpty(voyageId)) throw new ArgumentNullException(nameof(voyageId));
            if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
            if (pageSize < 1 || pageSize > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
            if (tenantId != null && tenantId.Length == 0) throw new ArgumentException("Tenant scope must not be empty.", nameof(tenantId));
            if (userId != null && (userId.Length == 0 || tenantId == null))
                throw new ArgumentException("User scope requires a nonempty user and tenant.", nameof(userId));
            VoyageMissionSummary result = new VoyageMissionSummary();
            result.Vessels.PageNumber = pageNumber;
            result.Vessels.PageSize = pageSize;
            using (DbCommand command = connection.CreateCommand())
            {
                string scope = "voyage_id = @voyage";
                Add(command, "@voyage", voyageId);
                if (tenantId != null) { scope += " AND tenant_id = @tenant"; Add(command, "@tenant", tenantId); }
                if (userId != null) { scope += " AND user_id = @user"; Add(command, "@user", userId); }
                Add(command, "@offset", ((long)pageNumber - 1) * pageSize);
                Add(command, "@limit", pageSize);
                string count = provider == DatabaseTypeEnum.SqlServer ? "COUNT_BIG(*)" : "COUNT(*)";
                string paging = provider == DatabaseTypeEnum.SqlServer
                    ? " OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY" : " LIMIT @limit OFFSET @offset";
                command.CommandText = "WITH visible AS (SELECT status, vessel_id FROM missions WHERE " + scope + "), "
                    + "vessels AS (SELECT DISTINCT vessel_id FROM visible WHERE vessel_id IS NOT NULL), "
                    + "paged AS (SELECT vessel_id FROM vessels ORDER BY vessel_id" + paging + ") "
                    + "SELECT 'status' AS kind, status, NULL AS vessel_id, " + count + " AS total FROM visible GROUP BY status "
                    + "UNION ALL SELECT 'vessel-total', NULL, NULL, " + count + " FROM vessels "
                    + "UNION ALL SELECT 'vessel', NULL, vessel_id, 0 FROM paged ORDER BY kind, vessel_id;";
                await connection.OpenAsync(token).ConfigureAwait(false);
                using (DbDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        string kind = (string)reader["kind"];
                        if (kind == "status")
                            result.StatusCounts.Add(Enum.Parse<MissionStatusEnum>((string)reader["status"]), Convert.ToInt64(reader["total"]));
                        else if (kind == "vessel") result.Vessels.Objects.Add((string)reader["vessel_id"]);
                        else result.Vessels.TotalRecords = Convert.ToInt64(reader["total"]);
                    }
                }
            }
            result.Vessels.TotalPages = checked((int)Math.Ceiling((double)result.Vessels.TotalRecords / pageSize));
            return result;
        }

        private static void Add(DbCommand command, string name, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}
