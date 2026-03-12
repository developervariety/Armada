namespace Armada.Core.Database.SqlServer.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// SQL Server implementation of fleet database operations.
    /// </summary>
    public class FleetMethods : IFleetMethods
    {
        #region Public-Members

        #endregion

        #region Private-Members

        private string _ConnectionString;
        private static readonly string _Iso8601Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate fleet methods for SQL Server.
        /// </summary>
        /// <param name="connectionString">SQL Server connection string.</param>
        public FleetMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create a fleet.
        /// </summary>
        /// <param name="fleet">Fleet to create.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Created fleet.</returns>
        public async Task<Fleet> CreateAsync(Fleet fleet, CancellationToken token = default)
        {
            if (fleet == null) throw new ArgumentNullException(nameof(fleet));
            fleet.LastUpdateUtc = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO fleets (id, name, description, active, created_utc, last_update_utc)
                        VALUES (@id, @name, @description, @active, @created_utc, @last_update_utc);";
                    cmd.Parameters.AddWithValue("@id", fleet.Id);
                    cmd.Parameters.AddWithValue("@name", fleet.Name);
                    cmd.Parameters.AddWithValue("@description", (object?)fleet.Description ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@active", fleet.Active);
                    cmd.Parameters.AddWithValue("@created_utc", ToIso8601(fleet.CreatedUtc));
                    cmd.Parameters.AddWithValue("@last_update_utc", ToIso8601(fleet.LastUpdateUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return fleet;
        }

        /// <summary>
        /// Read a fleet by identifier.
        /// </summary>
        /// <param name="id">Fleet identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Fleet or null if not found.</returns>
        public async Task<Fleet?> ReadAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqlConnection conn = new SqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM fleets WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return FleetFromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Read a fleet by name.
        /// </summary>
        /// <param name="name">Fleet name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Fleet or null if not found.</returns>
        public async Task<Fleet?> ReadByNameAsync(string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (SqlConnection conn = new SqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM fleets WHERE name = @name;";
                    cmd.Parameters.AddWithValue("@name", name);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return FleetFromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Update a fleet.
        /// </summary>
        /// <param name="fleet">Fleet to update.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Updated fleet.</returns>
        public async Task<Fleet> UpdateAsync(Fleet fleet, CancellationToken token = default)
        {
            if (fleet == null) throw new ArgumentNullException(nameof(fleet));
            fleet.LastUpdateUtc = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE fleets SET
                        name = @name,
                        description = @description,
                        active = @active,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", fleet.Id);
                    cmd.Parameters.AddWithValue("@name", fleet.Name);
                    cmd.Parameters.AddWithValue("@description", (object?)fleet.Description ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@active", fleet.Active);
                    cmd.Parameters.AddWithValue("@last_update_utc", ToIso8601(fleet.LastUpdateUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return fleet;
        }

        /// <summary>
        /// Delete a fleet by identifier.
        /// </summary>
        /// <param name="id">Fleet identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqlConnection conn = new SqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM fleets WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Enumerate all fleets.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of all fleets.</returns>
        public async Task<List<Fleet>> EnumerateAsync(CancellationToken token = default)
        {
            List<Fleet> results = new List<Fleet>();

            using (SqlConnection conn = new SqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM fleets ORDER BY name;";
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(FleetFromReader(reader));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Enumerate fleets with pagination and filtering.
        /// </summary>
        /// <param name="query">Enumeration query parameters.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Paginated enumeration result.</returns>
        public async Task<EnumerationResult<Fleet>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default)
        {
            if (query == null) query = new EnumerationQuery();

            using (SqlConnection conn = new SqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<SqlParameter> parameters = new List<SqlParameter>();

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(new SqlParameter("@created_after", ToIso8601(query.CreatedAfter.Value)));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(new SqlParameter("@created_before", ToIso8601(query.CreatedBefore.Value)));
                }

                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                // Count
                long totalCount = 0;
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM fleets" + whereClause + ";";
                    foreach (SqlParameter p in parameters) cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                // Query
                List<Fleet> results = new List<Fleet>();
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM fleets" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " OFFSET " + query.Offset + " ROWS FETCH NEXT " + query.PageSize + " ROWS ONLY;";
                    foreach (SqlParameter p in parameters) cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(FleetFromReader(reader));
                    }
                }

                return EnumerationResult<Fleet>.Create(query, results, totalCount);
            }
        }

        /// <summary>
        /// Check if a fleet exists by identifier.
        /// </summary>
        /// <param name="id">Fleet identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the fleet exists.</returns>
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqlConnection conn = new SqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM fleets WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    int count = Convert.ToInt32(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                    return count > 0;
                }
            }
        }

        #endregion

        #region Private-Methods

        private static string ToIso8601(DateTime dt)
        {
            return dt.ToUniversalTime().ToString(_Iso8601Format, CultureInfo.InvariantCulture);
        }

        private static DateTime FromIso8601(string value)
        {
            return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        }

        private static DateTime? FromIso8601Nullable(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            string str = value.ToString()!;
            if (string.IsNullOrEmpty(str)) return null;
            return FromIso8601(str);
        }

        private static string? NullableString(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            string str = value.ToString()!;
            return string.IsNullOrEmpty(str) ? null : str;
        }

        private static Fleet FleetFromReader(SqlDataReader reader)
        {
            Fleet fleet = new Fleet();
            fleet.Id = reader["id"].ToString()!;
            fleet.Name = reader["name"].ToString()!;
            fleet.Description = NullableString(reader["description"]);
            fleet.Active = Convert.ToBoolean(reader["active"]);
            fleet.CreatedUtc = FromIso8601(reader["created_utc"].ToString()!);
            fleet.LastUpdateUtc = FromIso8601(reader["last_update_utc"].ToString()!);
            return fleet;
        }

        #endregion
    }
}
