namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Paginated mission enumeration and Mission-shaped summary reads. Summaries select
    /// <see cref="MissionSummaryProjection.Columns"/>, so the heavy text columns never leave the database; full and
    /// summary lists share one filter set at every scope.
    /// </summary>
    public partial class MissionMethods
    {
        /// <inheritdoc />
        public async Task<Mission?> ReadSummaryAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT " + MissionSummaryProjection.Columns + " FROM missions WHERE id = @id LIMIT 1;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return MissionFromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public Task<EnumerationResult<Mission>> EnumerateSummariesAsync(EnumerationQuery query, CancellationToken token = default)
        {
            return EnumerateMissionRowsAsync(MissionSummaryProjection.Columns, null, null, query, token);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<Mission>> EnumerateSummariesAsync(string tenantId, EnumerationQuery query, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            return EnumerateMissionRowsAsync(MissionSummaryProjection.Columns, tenantId, null, query, token);
        }

        /// <inheritdoc />
        public Task<EnumerationResult<Mission>> EnumerateSummariesAsync(string tenantId, string userId, EnumerationQuery query, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            return EnumerateMissionRowsAsync(MissionSummaryProjection.Columns, tenantId, userId, query, token);
        }

        /// <inheritdoc />
        public async Task<Dictionary<MissionStatusEnum, int>> CountByVoyageStatusAsync(string voyageId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(voyageId)) throw new ArgumentNullException(nameof(voyageId));

            Dictionary<MissionStatusEnum, int> results = new Dictionary<MissionStatusEnum, int>();
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT status, COUNT(*) AS cnt FROM missions WHERE voyage_id = @voyage_id GROUP BY status;";
                    cmd.Parameters.AddWithValue("@voyage_id", voyageId);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
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

        /// <summary>
        /// Every paginated mission enumeration, full or summary, at every scope: one filter set, so a scoped
        /// read applies exactly the filters an unscoped read does, plus its tenant and user.
        /// </summary>
        private async Task<EnumerationResult<Mission>> EnumerateMissionRowsAsync(string selectColumns, string? tenantId, string? userId, EnumerationQuery? query, CancellationToken token)
        {
            if (query == null) query = new EnumerationQuery();

            List<string> conditions = new List<string>();
            List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
            if (!String.IsNullOrEmpty(tenantId))
            {
                conditions.Add("tenant_id = @tenantId");
                parameters.Add(new NpgsqlParameter("@tenantId", tenantId));
            }
            if (!String.IsNullOrEmpty(userId))
            {
                conditions.Add("user_id = @userId");
                parameters.Add(new NpgsqlParameter("@userId", userId));
            }
            if (query.CreatedAfter.HasValue)
            {
                conditions.Add("created_utc > @created_after");
                parameters.Add(new NpgsqlParameter("@created_after", query.CreatedAfter.Value));
            }
            if (query.CreatedBefore.HasValue)
            {
                conditions.Add("created_utc < @created_before");
                parameters.Add(new NpgsqlParameter("@created_before", query.CreatedBefore.Value));
            }
            if (!String.IsNullOrEmpty(query.Status))
            {
                conditions.Add("status = @status");
                parameters.Add(new NpgsqlParameter("@status", query.Status));
            }
            if (!String.IsNullOrEmpty(query.VoyageId))
            {
                conditions.Add("voyage_id = @voyage_id");
                parameters.Add(new NpgsqlParameter("@voyage_id", query.VoyageId));
            }
            if (!String.IsNullOrEmpty(query.MissionId))
            {
                conditions.Add("id = @mission_id");
                parameters.Add(new NpgsqlParameter("@mission_id", query.MissionId));
            }
            if (!String.IsNullOrEmpty(query.VesselId))
            {
                conditions.Add("vessel_id = @vessel_id");
                parameters.Add(new NpgsqlParameter("@vessel_id", query.VesselId));
            }
            if (!String.IsNullOrEmpty(query.CaptainId))
            {
                conditions.Add("captain_id = @captain_id");
                parameters.Add(new NpgsqlParameter("@captain_id", query.CaptainId));
            }

            string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : "";
            string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                long totalCount;
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM missions" + whereClause + ";";
                    foreach (NpgsqlParameter p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<Mission> results = new List<Mission>();
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT " + selectColumns + " FROM missions" + whereClause +
                        " ORDER BY created_utc " + orderDirection + " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (NpgsqlParameter p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MissionFromReader(reader));
                    }
                }

                return EnumerationResult<Mission>.Create(query, results, totalCount);
            }
        }
    }
}
