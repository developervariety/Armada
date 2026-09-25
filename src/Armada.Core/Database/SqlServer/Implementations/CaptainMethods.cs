namespace Armada.Core.Database.SqlServer.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// SQL Server implementation of captain database operations.
    /// </summary>
    public class CaptainMethods : ICaptainMethods
    {
        #region Private-Members

#pragma warning disable CS0414
        private readonly string _Header = "[CaptainMethods] ";
#pragma warning restore CS0414
        private readonly SqlServerDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate captain methods for SQL Server.
        /// </summary>
        /// <param name="driver">SQL Server database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        public CaptainMethods(SqlServerDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<Captain> CreateAsync(Captain captain, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            captain.LastUpdateUtc = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO captains (id, tenant_id, user_id, name, runtime, model, model_endpoint_id, api_key, api_base_url, system_instructions, allowed_personas, preferred_persona, runtime_options_json, default_playbooks, state, current_mission_id, current_dock_id, process_id, process_started_utc, recovery_attempts, last_heartbeat_utc, quarantine_until_utc, quarantine_reason, created_utc, last_update_utc, tier, preference_rank)
                        VALUES (@id, @tenant_id, @user_id, @name, @runtime, @model, @model_endpoint_id, @api_key, @api_base_url, @system_instructions, @allowed_personas, @preferred_persona, @runtime_options_json, @default_playbooks, @state, @current_mission_id, @current_dock_id, @process_id, @process_started_utc, @recovery_attempts, @last_heartbeat_utc, @quarantine_until_utc, @quarantine_reason, @created_utc, @last_update_utc, @tier, @preference_rank);";
                    CaptainColumns.Write(SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains"), captain);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return captain;
        }

        /// <inheritdoc />
        public async Task<Captain?> ReadAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return CaptainColumns.Read(reader, SqlServerDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<Captain?> ReadByNameAsync(string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE name = @name;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return CaptainColumns.Read(reader, SqlServerDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<Captain> UpdateAsync(Captain captain, CancellationToken token = default)
        {
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            captain.LastUpdateUtc = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET tier = @tier, preference_rank = @preference_rank,
                        tenant_id = @tenant_id,
                            user_id = @user_id,
                        name = @name,
                        runtime = @runtime,
                        model = @model,
                            model_endpoint_id = @model_endpoint_id,                        api_key = @api_key,
                        api_base_url = @api_base_url,
                        system_instructions = @system_instructions,
                        allowed_personas = @allowed_personas,
                        preferred_persona = @preferred_persona,
                        runtime_options_json = @runtime_options_json,
                        default_playbooks = @default_playbooks,
                        state = @state,
                        current_mission_id = @current_mission_id,
                        current_dock_id = @current_dock_id,
                        process_id = @process_id, process_started_utc = @process_started_utc,
                        recovery_attempts = @recovery_attempts,
                        last_heartbeat_utc = @last_heartbeat_utc,
                        quarantine_until_utc = @quarantine_until_utc,
                        quarantine_reason = @quarantine_reason,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    CaptainColumns.Write(SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains"), captain);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return captain;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            await DeleteCaptainAndSignalReferencesAsync("id = @id", token, StoredValueBinder.Parameter(new SqlParameter(), "@id", id)).ConfigureAwait(false);
        }

        /// <summary>
        /// Delete the captains matched by a scope and clear the signal references to them, as one transaction.
        /// SQL Server forbids two ON DELETE SET NULL foreign keys from one table to the same parent ("multiple
        /// cascade paths"), so the signals-to-captains keys are NO ACTION and the null-on-delete that the other
        /// providers declare in the schema is done here. The references are cleared only for captains the scope
        /// matches, and a delete that fails (a referencing row elsewhere) rolls the cleared references back.
        /// </summary>
        private async Task DeleteCaptainAndSignalReferencesAsync(string scope, CancellationToken token, params SqlParameter[] parameters)
        {
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlTransaction transaction = (SqlTransaction)await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    using (SqlCommand cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = transaction;
                        cmd.CommandText =
                            "UPDATE signals SET from_captain_id = NULL WHERE from_captain_id IN (SELECT id FROM captains WHERE " + scope + "); " +
                            "UPDATE signals SET to_captain_id = NULL WHERE to_captain_id IN (SELECT id FROM captains WHERE " + scope + "); " +
                            "DELETE FROM captains WHERE " + scope + ";";
                        foreach (SqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<Captain>> EnumerateAsync(CancellationToken token = default)
        {
            List<Captain> results = new List<Captain>();

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains ORDER BY name;";
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CaptainColumns.Read(reader, SqlServerDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<List<Captain>> EnumerateByStateAsync(CaptainStateEnum state, CancellationToken token = default)
        {
            List<Captain> results = new List<Captain>();

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE state = @state ORDER BY name;";
                    StoredValueBinder.Value(cmd, "@state", state.ToString());
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CaptainColumns.Read(reader, SqlServerDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task UpdateStateAsync(string id, CaptainStateEnum state, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET state = @state, last_update_utc = @last_update_utc WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    StoredValueBinder.Value(cmd, "@state", state.ToString());
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@last_update_utc", "last_update_utc", DateTime.UtcNow);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task UpdateHeartbeatAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            DateTime now = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET last_heartbeat_utc = @last_heartbeat_utc, last_update_utc = @last_update_utc WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@last_heartbeat_utc", "last_heartbeat_utc", now);
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@last_update_utc", "last_update_utc", now);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Update the captain's process-liveness timestamp without advancing the output heartbeat.
        /// Refreshed while the agent's OS process is alive but silent, so liveness telemetry stays
        /// current without masking a stall (which is measured from the output heartbeat).
        /// </summary>
        /// <param name="id">Captain identifier.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task UpdateProcessAliveAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            DateTime now = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET last_process_alive_utc = @last_process_alive_utc, last_update_utc = @last_update_utc WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@last_process_alive_utc", "last_process_alive_utc", now);
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@last_update_utc", "last_update_utc", now);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<Captain>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default)
        {
            if (query == null) query = new EnumerationQuery();

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<SqlParameter> parameters = new List<SqlParameter>();

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(SqlServerDatabaseDriver.StoredBinder.Timestamp(new SqlParameter(), "@created_after", "captains", "created_utc", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(SqlServerDatabaseDriver.StoredBinder.Timestamp(new SqlParameter(), "@created_before", "captains", "created_utc", query.CreatedBefore.Value));
                }
                if (!string.IsNullOrEmpty(query.Status))
                {
                    conditions.Add("state = @state");
                    parameters.Add(StoredValueBinder.Parameter(new SqlParameter(), "@state", query.Status));
                }

                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                // Count
                long totalCount = 0;
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM captains" + whereClause + ";";
                    foreach (SqlParameter p in parameters) cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                // Query
                List<Captain> results = new List<Captain>();
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " OFFSET " + query.Offset + " ROWS FETCH NEXT " + query.PageSize + " ROWS ONLY;";
                    foreach (SqlParameter p in parameters) cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CaptainColumns.Read(reader, SqlServerDatabaseDriver.StoredValues));
                    }
                }

                return EnumerationResult<Captain>.Create(query, results, totalCount);
            }
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM captains WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    int count = Convert.ToInt32(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                    return count > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<Captain?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE tenant_id = @tenantId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return CaptainColumns.Read(reader, SqlServerDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            await DeleteCaptainAndSignalReferencesAsync("tenant_id = @tenantId AND id = @id", token, StoredValueBinder.Parameter(new SqlParameter(), "@tenantId", tenantId), StoredValueBinder.Parameter(new SqlParameter(), "@id", id)).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<Captain>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            List<Captain> results = new List<Captain>();

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE tenant_id = @tenantId ORDER BY name;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CaptainColumns.Read(reader, SqlServerDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<Captain?> ReadByNameAsync(string tenantId, string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE tenant_id = @tenantId AND name = @name;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@name", name);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return CaptainColumns.Read(reader, SqlServerDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<List<Captain>> EnumerateByStateAsync(string tenantId, CaptainStateEnum state, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            List<Captain> results = new List<Captain>();

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE tenant_id = @tenantId AND state = @state ORDER BY name;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@state", state.ToString());
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CaptainColumns.Read(reader, SqlServerDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task UpdateStateAsync(string tenantId, string id, CaptainStateEnum state, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET state = @state, last_update_utc = @last_update_utc WHERE tenant_id = @tenantId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    StoredValueBinder.Value(cmd, "@state", state.ToString());
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@last_update_utc", "last_update_utc", DateTime.UtcNow);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task UpdateHeartbeatAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            DateTime now = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET last_heartbeat_utc = @last_heartbeat_utc, last_update_utc = @last_update_utc WHERE tenant_id = @tenantId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@last_heartbeat_utc", "last_heartbeat_utc", now);
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@last_update_utc", "last_update_utc", now);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM captains WHERE tenant_id = @tenantId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    int count = Convert.ToInt32(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                    return count > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryClaimAsync(string tenantId, string captainId, string missionId, string dockId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));
            if (string.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));
            if (string.IsNullOrEmpty(dockId)) throw new ArgumentNullException(nameof(dockId));

            DateTime now = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET
                        state = @state,
                        current_mission_id = @current_mission_id,
                        current_dock_id = @current_dock_id,
                        last_heartbeat_utc = @last_heartbeat_utc,
                        last_update_utc = @last_update_utc
                        WHERE (tenant_id = @tenantId OR (@unownedIsDefault = 1 AND tenant_id IS NULL)) AND id = @id AND state = 'Idle';";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@unownedIsDefault", String.Equals(tenantId, Armada.Core.Constants.DefaultTenantId, StringComparison.Ordinal) ? 1 : 0);
                    StoredValueBinder.Value(cmd, "@id", captainId);
                    StoredValueBinder.Value(cmd, "@state", CaptainStateEnum.Working.ToString());
                    StoredValueBinder.Value(cmd, "@current_mission_id", missionId);
                    StoredValueBinder.Value(cmd, "@current_dock_id", dockId);
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@last_heartbeat_utc", "last_heartbeat_utc", now);
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@last_update_utc", "last_update_utc", now);
                    int rowsAffected = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return rowsAffected > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<Captain>> EnumerateAsync(string tenantId, EnumerationQuery query, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (query == null) query = new EnumerationQuery();

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string> { "tenant_id = @tenantId" };
                List<SqlParameter> parameters = new List<SqlParameter> { StoredValueBinder.Parameter(new SqlParameter(), "@tenantId", tenantId) };

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(SqlServerDatabaseDriver.StoredBinder.Timestamp(new SqlParameter(), "@created_after", "captains", "created_utc", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(SqlServerDatabaseDriver.StoredBinder.Timestamp(new SqlParameter(), "@created_before", "captains", "created_utc", query.CreatedBefore.Value));
                }

                string whereClause = " WHERE " + string.Join(" AND ", conditions);
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                long totalCount = 0;
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM captains" + whereClause + ";";
                    foreach (SqlParameter p in parameters) cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<Captain> results = new List<Captain>();
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains" + whereClause + " ORDER BY created_utc " + orderDirection + " OFFSET " + query.Offset + " ROWS FETCH NEXT " + query.PageSize + " ROWS ONLY;";
                    foreach (SqlParameter p in parameters) cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CaptainColumns.Read(reader, SqlServerDatabaseDriver.StoredValues));
                    }
                }

                return EnumerationResult<Captain>.Create(query, results, totalCount);
            }
        }

        /// <inheritdoc />
        public async Task<Captain?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE tenant_id = @tenantId AND user_id = @userId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@userId", userId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return CaptainColumns.Read(reader, SqlServerDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            await DeleteCaptainAndSignalReferencesAsync("tenant_id = @tenantId AND user_id = @userId AND id = @id", token, StoredValueBinder.Parameter(new SqlParameter(), "@tenantId", tenantId), StoredValueBinder.Parameter(new SqlParameter(), "@userId", userId), StoredValueBinder.Parameter(new SqlParameter(), "@id", id)).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<Captain>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            List<Captain> results = new List<Captain>();

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE tenant_id = @tenantId AND user_id = @userId ORDER BY name;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@userId", userId);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CaptainColumns.Read(reader, SqlServerDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<Captain>> EnumerateAsync(string tenantId, string userId, EnumerationQuery query, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            if (query == null) query = new EnumerationQuery();

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string> { "tenant_id = @tenantId", "user_id = @userId" };
                List<SqlParameter> parameters = new List<SqlParameter>
                {
                    StoredValueBinder.Parameter(new SqlParameter(), "@tenantId", tenantId),
                    StoredValueBinder.Parameter(new SqlParameter(), "@userId", userId)
                };

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(SqlServerDatabaseDriver.StoredBinder.Timestamp(new SqlParameter(), "@created_after", "captains", "created_utc", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(SqlServerDatabaseDriver.StoredBinder.Timestamp(new SqlParameter(), "@created_before", "captains", "created_utc", query.CreatedBefore.Value));
                }

                string whereClause = " WHERE " + string.Join(" AND ", conditions);
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                long totalCount = 0;
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM captains" + whereClause + ";";
                    foreach (SqlParameter p in parameters) cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<Captain> results = new List<Captain>();
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains" + whereClause + " ORDER BY created_utc " + orderDirection + " OFFSET " + query.Offset + " ROWS FETCH NEXT " + query.PageSize + " ROWS ONLY;";
                    foreach (SqlParameter p in parameters) cmd.Parameters.Add(new SqlParameter(p.ParameterName, p.Value));
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(CaptainColumns.Read(reader, SqlServerDatabaseDriver.StoredValues));
                    }
                }

                return EnumerationResult<Captain>.Create(query, results, totalCount);
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryQuarantineIdleAsync(string captainId, string reason, DateTime? untilUtc, CancellationToken token = default, bool preserveStrongerHold = false)
        {
            if (string.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentNullException(nameof(reason));

            DateTime now = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET
                        state = @state,
                        quarantine_until_utc = @quarantine_until_utc,
                        quarantine_reason = @quarantine_reason,
                        last_update_utc = @last_update_utc
                        WHERE id = @id AND state IN ('Idle', 'Quarantined')
                        AND current_mission_id IS NULL AND current_dock_id IS NULL AND process_id IS NULL
                        AND (@preserve_stronger_hold = 0 OR state = 'Idle' OR (quarantine_until_utc IS NOT NULL AND (@quarantine_until_utc IS NULL OR quarantine_until_utc < @quarantine_until_utc)));";
                    StoredValueBinder.Value(cmd, "@id", captainId);
                    StoredValueBinder.Value(cmd, "@state", CaptainStateEnum.Quarantined.ToString());
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@quarantine_until_utc", "quarantine_until_utc", untilUtc);
                    StoredValueBinder.Value(cmd, "@quarantine_reason", reason.Trim());
                    StoredValueBinder.Value(cmd, "@preserve_stronger_hold", preserveStrongerHold);
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@last_update_utc", "last_update_utc", now);
                    int rowsAffected = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return rowsAffected > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryReleaseQuarantineAsync(string captainId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));

            DateTime now = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET
                        state = @state,
                        quarantine_until_utc = NULL,
                        quarantine_reason = NULL,
                        last_update_utc = @last_update_utc
                        WHERE id = @id AND state = 'Quarantined';";
                    StoredValueBinder.Value(cmd, "@id", captainId);
                    StoredValueBinder.Value(cmd, "@state", CaptainStateEnum.Idle.ToString());
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@last_update_utc", "last_update_utc", now);
                    int rowsAffected = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return rowsAffected > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryReleaseTimedQuarantineAsync(string captainId, DateTime? expiredAtOrBeforeUtc, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));

            DateTime now = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET
                        state = @state,
                        quarantine_until_utc = NULL,
                        quarantine_reason = NULL,
                        last_update_utc = @last_update_utc
                        WHERE id = @id AND state = 'Quarantined' AND quarantine_until_utc IS NOT NULL"
                        + (expiredAtOrBeforeUtc.HasValue ? " AND quarantine_until_utc <= @cutoff" : "") + ";";
                    StoredValueBinder.Value(cmd, "@id", captainId);
                    StoredValueBinder.Value(cmd, "@state", CaptainStateEnum.Idle.ToString());
                    SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@last_update_utc", "last_update_utc", now);
                    if (expiredAtOrBeforeUtc.HasValue)
                        SqlServerDatabaseDriver.StoredBinder.For(cmd, "captains").Utc("@cutoff", "quarantine_until_utc", expiredAtOrBeforeUtc.Value);
                    int rowsAffected = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return rowsAffected > 0;
                }
            }
        }

        #endregion
    }
}
