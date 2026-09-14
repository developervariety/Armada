namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// SQLite implementation of captain database operations.
    /// </summary>
    public class CaptainMethods : ICaptainMethods
    {
        #region Private-Members

#pragma warning disable CS0414
        private readonly string _Header = "[CaptainMethods] ";
#pragma warning restore CS0414
        private readonly SqliteDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="driver">SQLite database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        public CaptainMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
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

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO captains (id, tenant_id, user_id, name, runtime, model, model_endpoint_id, api_key, api_base_url, system_instructions, allowed_personas, preferred_persona, runtime_options_json, default_playbooks, state, current_mission_id, current_dock_id, process_id, recovery_attempts, last_heartbeat_utc, quarantine_until_utc, quarantine_reason, created_utc, last_update_utc, tier)
                            VALUES (@id, @tenant_id, @user_id, @name, @runtime, @model, @model_endpoint_id, @api_key, @api_base_url, @system_instructions, @allowed_personas, @preferred_persona, @runtime_options_json, @default_playbooks, @state, @current_mission_id, @current_dock_id, @process_id, @recovery_attempts, @last_heartbeat_utc, @quarantine_until_utc, @quarantine_reason, @created_utc, @last_update_utc, @tier);";
                    cmd.Parameters.AddWithValue("@id", captain.Id);
                    cmd.Parameters.AddWithValue("@tenant_id", (object?)captain.TenantId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@user_id", (object?)captain.UserId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@name", captain.Name);
                    cmd.Parameters.AddWithValue("@runtime", captain.Runtime.ToString());
                    BackendMetadataPersistence.AddCaptain(cmd, captain);
                    cmd.Parameters.AddWithValue("@model", (object?)captain.Model ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@model_endpoint_id", (object?)captain.ModelEndpointId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@api_key", (object?)captain.ApiKey ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@api_base_url", (object?)captain.ApiBaseUrl ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@system_instructions", (object?)captain.SystemInstructions ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@allowed_personas", (object?)captain.AllowedPersonas ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@preferred_persona", (object?)captain.PreferredPersona ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@runtime_options_json", (object?)captain.RuntimeOptionsJson ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@default_playbooks", (object?)captain.DefaultPlaybooks ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@state", captain.State.ToString());
                    cmd.Parameters.AddWithValue("@current_mission_id", (object?)captain.CurrentMissionId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@current_dock_id", (object?)captain.CurrentDockId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@process_id", captain.ProcessId.HasValue ? (object)captain.ProcessId.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@recovery_attempts", captain.RecoveryAttempts);
                    cmd.Parameters.AddWithValue("@last_heartbeat_utc", captain.LastHeartbeatUtc.HasValue ? (object)SqliteDatabaseDriver.ToIso8601(captain.LastHeartbeatUtc.Value) : DBNull.Value);
                    cmd.Parameters.AddWithValue("@quarantine_until_utc", captain.QuarantineUntilUtc.HasValue ? (object)SqliteDatabaseDriver.ToIso8601(captain.QuarantineUntilUtc.Value) : DBNull.Value);
                    cmd.Parameters.AddWithValue("@quarantine_reason", (object?)captain.QuarantineReason ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@created_utc", SqliteDatabaseDriver.ToIso8601(captain.CreatedUtc));
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(captain.LastUpdateUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return captain;
        }

        /// <inheritdoc />
        public async Task<Captain?> ReadAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return SqliteDatabaseDriver.CaptainFromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<Captain?> ReadByNameAsync(string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE name = @name;";
                    cmd.Parameters.AddWithValue("@name", name);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return SqliteDatabaseDriver.CaptainFromReader(reader);
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

            SqliteTransaction? transaction = _Driver.CurrentTransaction;
            if (transaction != null)
            {
                await UpdateAsync(transaction.Connection!, transaction).ConfigureAwait(false);
            }
            else
            {
                using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
                {
                    await conn.OpenAsync(token).ConfigureAwait(false);
                    await UpdateAsync(conn, null).ConfigureAwait(false);
                }
            }

            return captain;

            async Task UpdateAsync(SqliteConnection conn, SqliteTransaction? tx)
            {
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"UPDATE captains SET tier = @tier,
                            tenant_id = @tenant_id,
                            user_id = @user_id,
                            name = @name,
                            runtime = @runtime,
                            model = @model,
                            model_endpoint_id = @model_endpoint_id,                            api_key = @api_key,
                            api_base_url = @api_base_url,
                            system_instructions = @system_instructions,
                            allowed_personas = @allowed_personas,
                            preferred_persona = @preferred_persona,
                            runtime_options_json = @runtime_options_json,
                            default_playbooks = @default_playbooks,
                            state = @state,
                            current_mission_id = @current_mission_id,
                            current_dock_id = @current_dock_id,
                            process_id = @process_id,
                            recovery_attempts = @recovery_attempts,
                            last_heartbeat_utc = @last_heartbeat_utc,
                            quarantine_until_utc = @quarantine_until_utc,
                            quarantine_reason = @quarantine_reason,
                            last_update_utc = @last_update_utc
                            WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", captain.Id);
                    cmd.Parameters.AddWithValue("@tenant_id", (object?)captain.TenantId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@user_id", (object?)captain.UserId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@name", captain.Name);
                    cmd.Parameters.AddWithValue("@runtime", captain.Runtime.ToString());
                    BackendMetadataPersistence.AddCaptain(cmd, captain);
                    cmd.Parameters.AddWithValue("@model", (object?)captain.Model ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@model_endpoint_id", (object?)captain.ModelEndpointId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@api_key", (object?)captain.ApiKey ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@api_base_url", (object?)captain.ApiBaseUrl ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@system_instructions", (object?)captain.SystemInstructions ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@allowed_personas", (object?)captain.AllowedPersonas ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@preferred_persona", (object?)captain.PreferredPersona ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@runtime_options_json", (object?)captain.RuntimeOptionsJson ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@default_playbooks", (object?)captain.DefaultPlaybooks ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@state", captain.State.ToString());
                    cmd.Parameters.AddWithValue("@current_mission_id", (object?)captain.CurrentMissionId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@current_dock_id", (object?)captain.CurrentDockId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@process_id", captain.ProcessId.HasValue ? (object)captain.ProcessId.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@recovery_attempts", captain.RecoveryAttempts);
                    cmd.Parameters.AddWithValue("@last_heartbeat_utc", captain.LastHeartbeatUtc.HasValue ? (object)SqliteDatabaseDriver.ToIso8601(captain.LastHeartbeatUtc.Value) : DBNull.Value);
                    cmd.Parameters.AddWithValue("@quarantine_until_utc", captain.QuarantineUntilUtc.HasValue ? (object)SqliteDatabaseDriver.ToIso8601(captain.QuarantineUntilUtc.Value) : DBNull.Value);
                    cmd.Parameters.AddWithValue("@quarantine_reason", (object?)captain.QuarantineReason ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(captain.LastUpdateUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM captains WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<Captain>> EnumerateAsync(CancellationToken token = default)
        {
            List<Captain> results = new List<Captain>();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains ORDER BY name;";
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CaptainFromReader(reader));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<List<Captain>> EnumerateByStateAsync(CaptainStateEnum state, CancellationToken token = default)
        {
            List<Captain> results = new List<Captain>();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE state = @state ORDER BY name;";
                    cmd.Parameters.AddWithValue("@state", state.ToString());
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CaptainFromReader(reader));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task UpdateStateAsync(string id, CaptainStateEnum state, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET state = @state, last_update_utc = @last_update_utc WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@state", state.ToString());
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(DateTime.UtcNow));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task UpdateHeartbeatAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            DateTime now = DateTime.UtcNow;

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET last_heartbeat_utc = @last_heartbeat_utc, last_update_utc = @last_update_utc WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@last_heartbeat_utc", SqliteDatabaseDriver.ToIso8601(now));
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(now));
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

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET last_process_alive_utc = @last_process_alive_utc, last_update_utc = @last_update_utc WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@last_process_alive_utc", SqliteDatabaseDriver.ToIso8601(now));
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(now));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<Captain>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default)
        {
            if (query == null) query = new EnumerationQuery();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<SqliteParameter> parameters = new List<SqliteParameter>();

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(new SqliteParameter("@created_after", SqliteDatabaseDriver.ToIso8601(query.CreatedAfter.Value)));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(new SqliteParameter("@created_before", SqliteDatabaseDriver.ToIso8601(query.CreatedBefore.Value)));
                }
                if (!string.IsNullOrEmpty(query.Status))
                {
                    conditions.Add("state = @state");
                    parameters.Add(new SqliteParameter("@state", query.Status));
                }

                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                // Count
                long totalCount = 0;
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM captains" + whereClause + ";";
                    foreach (SqliteParameter p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                    totalCount = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                }

                // Query
                List<Captain> results = new List<Captain>();
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (SqliteParameter p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CaptainFromReader(reader));
                    }
                }

                return EnumerationResult<Captain>.Create(query, results, totalCount);
            }
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM captains WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    long count = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                    return count > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryQuarantineIdleAsync(string captainId, string reason, DateTime? untilUtc, CancellationToken token = default, bool preserveStrongerHold = false)
        {
            if (string.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentNullException(nameof(reason));

            DateTime now = DateTime.UtcNow;

            SqliteTransaction? transaction = _Driver.CurrentTransaction;
            if (transaction != null)
            {
                return await TryQuarantineAsync(transaction.Connection!, transaction).ConfigureAwait(false);
            }

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                return await TryQuarantineAsync(conn, null).ConfigureAwait(false);
            }

            async Task<bool> TryQuarantineAsync(SqliteConnection conn, SqliteTransaction? tx)
            {
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"UPDATE captains SET
                            state = @state,
                            quarantine_until_utc = @quarantine_until_utc,
                            quarantine_reason = @quarantine_reason,
                            last_update_utc = @last_update_utc
                            WHERE id = @id AND state IN ('Idle', 'Quarantined')
                            AND current_mission_id IS NULL AND current_dock_id IS NULL AND process_id IS NULL
                            AND (@preserve_stronger_hold = 0 OR state = 'Idle' OR (quarantine_until_utc IS NOT NULL AND (@quarantine_until_utc IS NULL OR quarantine_until_utc < @quarantine_until_utc)));";
                    cmd.Parameters.AddWithValue("@id", captainId);
                    cmd.Parameters.AddWithValue("@state", CaptainStateEnum.Quarantined.ToString());
                    cmd.Parameters.AddWithValue("@quarantine_until_utc", untilUtc.HasValue ? (object)SqliteDatabaseDriver.ToIso8601(untilUtc.Value) : DBNull.Value);
                    cmd.Parameters.AddWithValue("@quarantine_reason", reason.Trim());
                    cmd.Parameters.AddWithValue("@preserve_stronger_hold", preserveStrongerHold ? 1 : 0);
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(now));
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

            SqliteTransaction? transaction = _Driver.CurrentTransaction;
            if (transaction != null)
            {
                return await TryReleaseAsync(transaction.Connection!, transaction).ConfigureAwait(false);
            }

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                return await TryReleaseAsync(conn, null).ConfigureAwait(false);
            }

            async Task<bool> TryReleaseAsync(SqliteConnection conn, SqliteTransaction? tx)
            {
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"UPDATE captains SET
                            state = @state,
                            quarantine_until_utc = NULL,
                            quarantine_reason = NULL,
                            last_update_utc = @last_update_utc
                            WHERE id = @id AND state = 'Quarantined';";
                    cmd.Parameters.AddWithValue("@id", captainId);
                    cmd.Parameters.AddWithValue("@state", CaptainStateEnum.Idle.ToString());
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(now));
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

            SqliteTransaction? transaction = _Driver.CurrentTransaction;
            if (transaction != null)
            {
                return await TryReleaseTimedAsync(transaction.Connection!, transaction).ConfigureAwait(false);
            }

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                return await TryReleaseTimedAsync(conn, null).ConfigureAwait(false);
            }

            async Task<bool> TryReleaseTimedAsync(SqliteConnection conn, SqliteTransaction? tx)
            {
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"UPDATE captains SET
                            state = @state,
                            quarantine_until_utc = NULL,
                            quarantine_reason = NULL,
                            last_update_utc = @last_update_utc
                            WHERE id = @id AND state = 'Quarantined' AND quarantine_until_utc IS NOT NULL"
                        + (expiredAtOrBeforeUtc.HasValue ? " AND quarantine_until_utc <= @cutoff" : "") + ";";
                    cmd.Parameters.AddWithValue("@id", captainId);
                    cmd.Parameters.AddWithValue("@state", CaptainStateEnum.Idle.ToString());
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(now));
                    if (expiredAtOrBeforeUtc.HasValue)
                        cmd.Parameters.AddWithValue("@cutoff", SqliteDatabaseDriver.ToIso8601(expiredAtOrBeforeUtc.Value));
                    int rowsAffected = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return rowsAffected > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> TryClaimAsync(string captainId, string missionId, string dockId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));
            if (string.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));
            if (string.IsNullOrEmpty(dockId)) throw new ArgumentNullException(nameof(dockId));

            DateTime now = DateTime.UtcNow;

            SqliteTransaction? transaction = _Driver.CurrentTransaction;
            if (transaction != null)
            {
                return await TryClaimAsync(transaction.Connection!, transaction).ConfigureAwait(false);
            }

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                return await TryClaimAsync(conn, null).ConfigureAwait(false);
            }

            async Task<bool> TryClaimAsync(SqliteConnection conn, SqliteTransaction? tx)
            {
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"UPDATE captains SET
                            state = @state,
                            current_mission_id = @current_mission_id,
                            current_dock_id = @current_dock_id,
                            last_heartbeat_utc = @last_heartbeat_utc,
                            last_update_utc = @last_update_utc
                            WHERE id = @id AND state = 'Idle';";
                    cmd.Parameters.AddWithValue("@id", captainId);
                    cmd.Parameters.AddWithValue("@state", CaptainStateEnum.Working.ToString());
                    cmd.Parameters.AddWithValue("@current_mission_id", missionId);
                    cmd.Parameters.AddWithValue("@current_dock_id", dockId);
                    cmd.Parameters.AddWithValue("@last_heartbeat_utc", SqliteDatabaseDriver.ToIso8601(now));
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(now));
                    int rowsAffected = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return rowsAffected > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<Captain?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE tenant_id = @tenantId AND id = @id;";
                    cmd.Parameters.AddWithValue("@tenantId", tenantId);
                    cmd.Parameters.AddWithValue("@id", id);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return SqliteDatabaseDriver.CaptainFromReader(reader);
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
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM captains WHERE tenant_id = @tenantId AND id = @id;";
                    cmd.Parameters.AddWithValue("@tenantId", tenantId);
                    cmd.Parameters.AddWithValue("@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<Captain>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            List<Captain> results = new List<Captain>();
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE tenant_id = @tenantId ORDER BY name;";
                    cmd.Parameters.AddWithValue("@tenantId", tenantId);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CaptainFromReader(reader));
                    }
                }
            }
            return results;
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<Captain>> EnumerateAsync(string tenantId, EnumerationQuery query, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (query == null) query = new EnumerationQuery();
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                List<string> conditions = new List<string> { "tenant_id = @tenantId" };
                List<SqliteParameter> parameters = new List<SqliteParameter> { new SqliteParameter("@tenantId", tenantId) };
                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(new SqliteParameter("@created_after", SqliteDatabaseDriver.ToIso8601(query.CreatedAfter.Value)));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(new SqliteParameter("@created_before", SqliteDatabaseDriver.ToIso8601(query.CreatedBefore.Value)));
                }
                string whereClause = " WHERE " + string.Join(" AND ", conditions);
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";
                long totalCount = 0;
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM captains" + whereClause + ";";
                    foreach (SqliteParameter p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                    totalCount = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                }
                List<Captain> results = new List<Captain>();
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains" + whereClause + " ORDER BY created_utc " + orderDirection + " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (SqliteParameter p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CaptainFromReader(reader));
                    }
                }
                return EnumerationResult<Captain>.Create(query, results, totalCount);
            }
        }

        /// <inheritdoc />
        public async Task<Captain?> ReadByNameAsync(string tenantId, string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE tenant_id = @tenantId AND name = @name;";
                    cmd.Parameters.AddWithValue("@tenantId", tenantId);
                    cmd.Parameters.AddWithValue("@name", name);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return SqliteDatabaseDriver.CaptainFromReader(reader);
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
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE tenant_id = @tenantId AND state = @state ORDER BY name;";
                    cmd.Parameters.AddWithValue("@tenantId", tenantId);
                    cmd.Parameters.AddWithValue("@state", state.ToString());
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CaptainFromReader(reader));
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
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET state = @state, last_update_utc = @last_update_utc WHERE tenant_id = @tenantId AND id = @id;";
                    cmd.Parameters.AddWithValue("@tenantId", tenantId);
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@state", state.ToString());
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(DateTime.UtcNow));
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
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE captains SET last_heartbeat_utc = @last_heartbeat_utc, last_update_utc = @last_update_utc WHERE tenant_id = @tenantId AND id = @id;";
                    cmd.Parameters.AddWithValue("@tenantId", tenantId);
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@last_heartbeat_utc", SqliteDatabaseDriver.ToIso8601(now));
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(now));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM captains WHERE tenant_id = @tenantId AND id = @id;";
                    cmd.Parameters.AddWithValue("@tenantId", tenantId);
                    cmd.Parameters.AddWithValue("@id", id);
                    long count = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
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

            SqliteTransaction? transaction = _Driver.CurrentTransaction;
            if (transaction != null)
            {
                return await TryClaimAsync(transaction.Connection!, transaction).ConfigureAwait(false);
            }

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                return await TryClaimAsync(conn, null).ConfigureAwait(false);
            }

            async Task<bool> TryClaimAsync(SqliteConnection conn, SqliteTransaction? tx)
            {
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"UPDATE captains SET
                            state = @state,
                            current_mission_id = @current_mission_id,
                            current_dock_id = @current_dock_id,
                            last_heartbeat_utc = @last_heartbeat_utc,
                            last_update_utc = @last_update_utc
                            WHERE tenant_id = @tenantId AND id = @id AND state = 'Idle';";
                    cmd.Parameters.AddWithValue("@tenantId", tenantId);
                    cmd.Parameters.AddWithValue("@id", captainId);
                    cmd.Parameters.AddWithValue("@state", CaptainStateEnum.Working.ToString());
                    cmd.Parameters.AddWithValue("@current_mission_id", missionId);
                    cmd.Parameters.AddWithValue("@current_dock_id", dockId);
                    cmd.Parameters.AddWithValue("@last_heartbeat_utc", SqliteDatabaseDriver.ToIso8601(now));
                    cmd.Parameters.AddWithValue("@last_update_utc", SqliteDatabaseDriver.ToIso8601(now));
                    int rowsAffected = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    return rowsAffected > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<Captain?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE tenant_id = @tenantId AND user_id = @userId AND id = @id;";
                    cmd.Parameters.AddWithValue("@tenantId", tenantId);
                    cmd.Parameters.AddWithValue("@userId", userId);
                    cmd.Parameters.AddWithValue("@id", id);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return SqliteDatabaseDriver.CaptainFromReader(reader);
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
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM captains WHERE tenant_id = @tenantId AND user_id = @userId AND id = @id;";
                    cmd.Parameters.AddWithValue("@tenantId", tenantId);
                    cmd.Parameters.AddWithValue("@userId", userId);
                    cmd.Parameters.AddWithValue("@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<Captain>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            List<Captain> results = new List<Captain>();
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains WHERE tenant_id = @tenantId AND user_id = @userId ORDER BY name;";
                    cmd.Parameters.AddWithValue("@tenantId", tenantId);
                    cmd.Parameters.AddWithValue("@userId", userId);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CaptainFromReader(reader));
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
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                List<string> conditions = new List<string> { "tenant_id = @tenantId", "user_id = @userId" };
                List<SqliteParameter> parameters = new List<SqliteParameter> { new SqliteParameter("@tenantId", tenantId), new SqliteParameter("@userId", userId) };
                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(new SqliteParameter("@created_after", SqliteDatabaseDriver.ToIso8601(query.CreatedAfter.Value)));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(new SqliteParameter("@created_before", SqliteDatabaseDriver.ToIso8601(query.CreatedBefore.Value)));
                }
                string whereClause = " WHERE " + string.Join(" AND ", conditions);
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";
                long totalCount = 0;
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM captains" + whereClause + ";";
                    foreach (SqliteParameter p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                    totalCount = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                }
                List<Captain> results = new List<Captain>();
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM captains" + whereClause + " ORDER BY created_utc " + orderDirection + " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (SqliteParameter p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SqliteDatabaseDriver.CaptainFromReader(reader));
                    }
                }
                return EnumerationResult<Captain>.Create(query, results, totalCount);
            }
        }

        #endregion
    }
}
