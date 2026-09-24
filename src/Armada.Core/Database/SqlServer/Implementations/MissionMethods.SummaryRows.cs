namespace Armada.Core.Database.SqlServer.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Mission-shaped summary reads. They select <see cref="MissionSummaryProjection.Columns"/>, so the heavy text
    /// columns never leave the database, and they apply every enumeration filter at every scope.
    /// </summary>
    internal partial class MissionMethods
    {
        /// <inheritdoc />
        public async Task<Mission?> ReadSummaryAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT TOP 1 " + MissionSummaryProjection.Columns + " FROM missions WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return SqlServerDatabaseDriver.MissionFromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public Task<EnumerationResult<Mission>> EnumerateSummariesAsync(EnumerationQuery query, CancellationToken token = default)
        {
            return EnumerateSummaryRowsAsync(null, null, query, token);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<Mission>> EnumerateSummariesAsync(string tenantId, EnumerationQuery query, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            return EnumerateSummaryRowsAsync(tenantId, null, query, token);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<Mission>> EnumerateSummariesAsync(string tenantId, string userId, EnumerationQuery query, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            return EnumerateSummaryRowsAsync(tenantId, userId, query, token);
        }

        /// <inheritdoc />
        public async Task<Dictionary<MissionStatusEnum, int>> CountByVoyageStatusAsync(string voyageId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(voyageId)) throw new ArgumentNullException(nameof(voyageId));

            Dictionary<MissionStatusEnum, int> results = new Dictionary<MissionStatusEnum, int>();
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT status, COUNT(*) AS cnt FROM missions WHERE voyage_id = @voyage_id GROUP BY status;";
                    cmd.Parameters.AddWithValue("@voyage_id", voyageId);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string? statusText = reader["status"] as string;
                            if (String.IsNullOrEmpty(statusText)) continue;
                            if (!Enum.TryParse(statusText, ignoreCase: false, out MissionStatusEnum parsed)) continue;
                            results[parsed] = Convert.ToInt32(reader["cnt"]);
                        }
                    }
                }
            }

            return results;
        }

        private async Task<EnumerationResult<Mission>> EnumerateSummaryRowsAsync(string? tenantId, string? userId, EnumerationQuery? query, CancellationToken token)
        {
            if (query == null) query = new EnumerationQuery();

            List<string> conditions = new List<string>();
            List<SqlParameter> parameters = new List<SqlParameter>();
            if (!String.IsNullOrEmpty(tenantId))
            {
                conditions.Add("tenant_id = @tenantId");
                parameters.Add(new SqlParameter("@tenantId", tenantId));
            }
            if (!String.IsNullOrEmpty(userId))
            {
                conditions.Add("user_id = @userId");
                parameters.Add(new SqlParameter("@userId", userId));
            }
            if (query.CreatedAfter.HasValue)
            {
                conditions.Add("created_utc > @created_after");
                parameters.Add(new SqlParameter("@created_after", SqlServerDatabaseDriver.ToIso8601(query.CreatedAfter.Value)));
            }
            if (query.CreatedBefore.HasValue)
            {
                conditions.Add("created_utc < @created_before");
                parameters.Add(new SqlParameter("@created_before", SqlServerDatabaseDriver.ToIso8601(query.CreatedBefore.Value)));
            }
            if (!String.IsNullOrEmpty(query.Status))
            {
                conditions.Add("status = @status");
                parameters.Add(new SqlParameter("@status", query.Status));
            }
            if (!String.IsNullOrEmpty(query.VoyageId))
            {
                conditions.Add("voyage_id = @voyage_id");
                parameters.Add(new SqlParameter("@voyage_id", query.VoyageId));
            }
            if (!String.IsNullOrEmpty(query.MissionId))
            {
                conditions.Add("id = @mission_id");
                parameters.Add(new SqlParameter("@mission_id", query.MissionId));
            }
            if (!String.IsNullOrEmpty(query.VesselId))
            {
                conditions.Add("vessel_id = @vessel_id");
                parameters.Add(new SqlParameter("@vessel_id", query.VesselId));
            }
            if (!String.IsNullOrEmpty(query.CaptainId))
            {
                conditions.Add("captain_id = @captain_id");
                parameters.Add(new SqlParameter("@captain_id", query.CaptainId));
            }

            string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : "";
            string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                long totalCount;
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM missions" + whereClause + ";";
                    foreach (SqlParameter p in parameters) cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<Mission> results = new List<Mission>();
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT " + MissionSummaryProjection.Columns + " FROM missions" + whereClause +
                        " ORDER BY created_utc " + orderDirection + " OFFSET " + query.Offset + " ROWS FETCH NEXT " + query.PageSize + " ROWS ONLY;";
                    foreach (SqlParameter p in parameters) cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqlServerDatabaseDriver.MissionFromReader(reader));
                    }
                }

                return EnumerationResult<Mission>.Create(query, results, totalCount);
            }
        }
    }
}
