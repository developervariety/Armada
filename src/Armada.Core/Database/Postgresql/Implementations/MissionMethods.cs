namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// PostgreSQL implementation of mission database operations.
    /// </summary>
    public partial class MissionMethods : IMissionMethods
    {
        #region Private-Members

#pragma warning disable CS0414
        private string _Header = "[MissionMethods] ";
#pragma warning restore CS0414
        private PostgresqlDatabaseDriver _Driver;
        private DatabaseSettings _Settings;
        private LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="driver">PostgreSQL database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        public MissionMethods(PostgresqlDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create a mission.
        /// </summary>
        /// <param name="mission">Mission to create.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Created mission.</returns>
        public async Task<Mission> CreateAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            mission.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"INSERT INTO missions (id, tenant_id, user_id, voyage_id, vessel_id, captain_id, title, description,
                        status, mission_assignment_state, priority, parent_mission_id, branch_name, dock_id, process_id, process_started_utc,
                        pr_url, commit_hash, diff_snapshot, agent_output, persona, depends_on_mission_id, stage_order, failure_reason, reconciled_utc, reconciled_reason, total_runtime_ms, prestaged_files, preferred_model, capabilityhint, mission_mode, requires_review, review_deny_action, review_comment, reviewed_by_user_id, review_requested_utc, reviewed_utc, recovery_attempts, landing_retry_count, start_from_ref, last_recovery_action_utc, created_utc, started_utc, completed_utc, last_update_utc, retry_skip_captain_ids, tier, requested_captain_id, held_for_operator_review, held_for_operator_review_reason)
                        VALUES (@id, @tenant_id, @user_id, @voyage_id, @vessel_id, @captain_id, @title, @description,
                        @status, @mission_assignment_state, @priority, @parent_mission_id, @branch_name, @dock_id, @process_id, @process_started_utc,
                        @pr_url, @commit_hash, @diff_snapshot, @agent_output, @persona, @depends_on_mission_id, @stage_order, @failure_reason, @reconciled_utc, @reconciled_reason, @total_runtime_ms, @prestaged_files, @preferred_model, @capabilityhint, @mission_mode, @requires_review, @review_deny_action, @review_comment, @reviewed_by_user_id, @review_requested_utc, @reviewed_utc, @recovery_attempts, @landing_retry_count, @start_from_ref, @last_recovery_action_utc, @created_utc, @started_utc, @completed_utc, @last_update_utc, @retry_skip_captain_ids, @tier, @requested_captain_id, @held_for_operator_review, @held_for_operator_review_reason);";
                    AddMissionParameters(cmd, mission);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await TouchVoyageAsync(conn, mission.VoyageId, mission.LastUpdateUtc, token).ConfigureAwait(false);
            }

            return mission;
        }

        /// <summary>
        /// Read a mission by identifier.
        /// </summary>
        /// <param name="id">Mission identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Mission or null if not found.</returns>
        public async Task<Mission?> ReadAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM missions WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return MissionColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Update a mission.
        /// </summary>
        /// <param name="mission">Mission to update.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Updated mission.</returns>
        public async Task<Mission> UpdateAsync(Mission mission, CancellationToken token = default)
        {
            await UpdateCoreAsync(mission, null, token).ConfigureAwait(false);
            return mission;
        }

        /// <inheritdoc />
        public async Task<bool> TryUpdateIfStatusAsync(Mission mission, MissionStatusEnum expectedStatus, CancellationToken token = default)
        {
            return await UpdateCoreAsync(mission, expectedStatus, token).ConfigureAwait(false) > 0;
        }

        private async Task<int> UpdateCoreAsync(Mission mission, MissionStatusEnum? expectedStatus, CancellationToken token)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            DateTime previousUpdateUtc = mission.LastUpdateUtc;
            mission.LastUpdateUtc = DateTime.UtcNow;
            int affected = 0;

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "UPDATE missions SET " + MissionAdmissionPersistence.PreserveOwnerSql(DatabaseTypeEnum.Postgresql) + @", tier = @tier, requested_captain_id = @requested_captain_id, held_for_operator_review = @held_for_operator_review, held_for_operator_review_reason = @held_for_operator_review_reason,
                        tenant_id = @tenant_id,
                            user_id = @user_id,
                        voyage_id = @voyage_id, vessel_id = @vessel_id, captain_id = @captain_id,
                        title = @title, description = @description, status = @status,
                        mission_assignment_state = @mission_assignment_state,
                        priority = @priority, parent_mission_id = @parent_mission_id,
                        branch_name = @branch_name, dock_id = @dock_id, process_id = @process_id, process_started_utc = @process_started_utc,
                        pr_url = @pr_url, commit_hash = @commit_hash, diff_snapshot = @diff_snapshot,
                        agent_output = @agent_output,
                        persona = @persona, depends_on_mission_id = @depends_on_mission_id, stage_order = @stage_order,
                        failure_reason = @failure_reason, reconciled_utc = @reconciled_utc, reconciled_reason = @reconciled_reason, total_runtime_ms = @total_runtime_ms,
                        prestaged_files = @prestaged_files,
                        preferred_model = @preferred_model,
                        capabilityhint = @capabilityhint,
                        mission_mode = @mission_mode,
                        requires_review = @requires_review,
                        review_deny_action = @review_deny_action,
                        review_comment = @review_comment,
                        reviewed_by_user_id = @reviewed_by_user_id,
                        review_requested_utc = @review_requested_utc,
                        reviewed_utc = @reviewed_utc,
                         recovery_attempts = @recovery_attempts,
                         landing_retry_count = @landing_retry_count,
                         start_from_ref = @start_from_ref,
                         last_recovery_action_utc = @last_recovery_action_utc,
                         retry_skip_captain_ids = @retry_skip_captain_ids,
                         started_utc = @started_utc, completed_utc = @completed_utc,
                         last_update_utc = @last_update_utc
                         WHERE id = @id" + (expectedStatus.HasValue ? " AND status = @expected_status" : "") + ";";
                    AddMissionParameters(cmd, mission);
                    if (expectedStatus.HasValue) StoredValueBinder.Value(cmd, "@expected_status", expectedStatus.Value.ToString());
                    affected = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                if (affected > 0) await TouchVoyageAsync(conn, mission.VoyageId, mission.LastUpdateUtc, token).ConfigureAwait(false);
            }

            if (affected == 0) mission.LastUpdateUtc = previousUpdateUtc;
            return affected;
        }

        /// <summary>
        /// Update the mission heartbeat timestamp without rewriting the full record.
        /// </summary>
        /// <param name="id">Mission identifier.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task UpdateHeartbeatAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            DateTime lastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                string? voyageId = null;
                using (NpgsqlCommand lookup = new NpgsqlCommand())
                {
                    lookup.Connection = conn;
                    lookup.CommandText = "SELECT voyage_id FROM missions WHERE id = @id;";
                    StoredValueBinder.Value(lookup, "@id", id);
                    object? result = await lookup.ExecuteScalarAsync(token).ConfigureAwait(false);
                    if (result != null && result != DBNull.Value)
                        voyageId = result.ToString();
                }

                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "UPDATE missions SET admission_revision = admission_revision + 1, last_update_utc = @last_update_utc WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    PostgresqlDatabaseDriver.StoredBinder.For(cmd, "missions").Utc("@last_update_utc", "last_update_utc", lastUpdateUtc);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await TouchVoyageAsync(conn, voyageId, lastUpdateUtc, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Delete a mission by identifier.
        /// </summary>
        /// <param name="id">Mission identifier.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM missions WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Enumerate all missions.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of all missions.</returns>
        public async Task<List<Mission>> EnumerateAsync(CancellationToken token = default)
        {
            List<Mission> results = new List<Mission>();

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM missions ORDER BY created_utc DESC;";
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MissionColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Enumerate missions with pagination and filtering.
        /// </summary>
        /// <param name="query">Enumeration query parameters.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Paginated enumeration result.</returns>
        public Task<EnumerationResult<Mission>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default)
        {
            return EnumerateMissionRowsAsync("*", null, null, query, token);
        }

        /// <summary>
        /// Enumerate missions by voyage identifier.
        /// </summary>
        /// <param name="voyageId">Voyage identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of missions for the voyage.</returns>
        public async Task<List<Mission>> EnumerateByVoyageAsync(string voyageId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(voyageId)) throw new ArgumentNullException(nameof(voyageId));
            return await EnumerateByColumnAsync("voyage_id", voyageId, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate missions by vessel identifier.
        /// </summary>
        /// <param name="vesselId">Vessel identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of missions for the vessel.</returns>
        public async Task<List<Mission>> EnumerateByVesselAsync(string vesselId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            return await EnumerateByColumnAsync("vessel_id", vesselId, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate missions by captain identifier.
        /// </summary>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of missions for the captain.</returns>
        public async Task<List<Mission>> EnumerateByCaptainAsync(string captainId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));
            return await EnumerateByColumnAsync("captain_id", captainId, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Enumerate missions by status.
        /// </summary>
        /// <param name="status">Mission status to filter by.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of missions with the specified status.</returns>
        public async Task<List<Mission>> EnumerateByStatusAsync(MissionStatusEnum status, CancellationToken token = default)
        {
            return await EnumerateByColumnAsync("status", status.ToString(), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Dictionary<MissionStatusEnum, int>> CountByStatusAsync(CancellationToken token = default)
        {
            Dictionary<MissionStatusEnum, int> results = new Dictionary<MissionStatusEnum, int>();

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT status, COUNT(*) AS cnt FROM missions GROUP BY status;";
                    using (NpgsqlDataReader reader = (NpgsqlDataReader)await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string? statusText = reader["status"] as string;
                            if (string.IsNullOrEmpty(statusText)) continue;
                            if (!Enum.TryParse(statusText, ignoreCase: false, out MissionStatusEnum parsed)) continue;
                            int count = Convert.ToInt32(reader["cnt"]);
                            results[parsed] = count;
                        }
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<Dictionary<MissionStatusEnum, int>> CountByStatusAsync(string tenantId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            Dictionary<MissionStatusEnum, int> results = new Dictionary<MissionStatusEnum, int>();

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT status, COUNT(*) AS cnt FROM missions WHERE tenant_id = @tenantId GROUP BY status;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    using (NpgsqlDataReader reader = (NpgsqlDataReader)await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string? statusText = reader["status"] as string;
                            if (string.IsNullOrEmpty(statusText)) continue;
                            if (!Enum.TryParse(statusText, ignoreCase: false, out MissionStatusEnum parsed)) continue;
                            int count = Convert.ToInt32(reader["cnt"]);
                            results[parsed] = count;
                        }
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<List<ActiveWorkFootprint>> EnumerateActiveWorkFootprintsAsync(string? tenantId, CancellationToken token = default)
        {
            bool tenantScoped = !String.IsNullOrEmpty(tenantId);
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = ActiveWorkFootprintQuery.CommandText(tenantScoped);
                    if (tenantScoped) StoredValueBinder.Value(cmd, ActiveWorkFootprintQuery.TenantParameter, tenantId);
                    using (NpgsqlDataReader reader = (NpgsqlDataReader)await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        return await ActiveWorkFootprintQuery.ReadAsync(reader, token).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<ActiveMissionSummary>> GetActiveVesselSummariesAsync(string vesselId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            List<ActiveMissionSummary> results = new List<ActiveMissionSummary>();

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText =
                        "SELECT id, title, status FROM missions " +
                        "WHERE vessel_id = @vessel_id AND status IN ('Assigned','InProgress');";
                    StoredValueBinder.Value(cmd, "@vessel_id", vesselId);
                    using (NpgsqlDataReader reader = (NpgsqlDataReader)await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            string? statusText = reader["status"] as string;
                            if (string.IsNullOrEmpty(statusText)) continue;
                            if (!Enum.TryParse(statusText, ignoreCase: false, out MissionStatusEnum parsed)) continue;
                            results.Add(new ActiveMissionSummary
                            {
                                Id = reader["id"] as string ?? "",
                                Title = reader["title"] as string ?? "",
                                Status = parsed
                            });
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Check if a mission exists by identifier.
        /// </summary>
        /// <param name="id">Mission identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the mission exists.</returns>
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT COUNT(*) FROM missions WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    long count = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                    return count > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<Mission?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM missions WHERE tenant_id = @tenantId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return MissionColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
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
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM missions WHERE tenant_id = @tenantId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<Mission>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            List<Mission> results = new List<Mission>();
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM missions WHERE tenant_id = @tenantId ORDER BY created_utc DESC;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MissionColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }
            return results;
        }

        /// <inheritdoc />
        public Task<EnumerationResult<Mission>> EnumerateAsync(string tenantId, EnumerationQuery query, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            return EnumerateMissionRowsAsync("*", tenantId, null, query, token);
        }

        /// <inheritdoc />
        public async Task<List<Mission>> EnumerateByVoyageAsync(string tenantId, string voyageId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(voyageId)) throw new ArgumentNullException(nameof(voyageId));
            return await EnumerateByTenantColumnAsync(tenantId, "voyage_id", voyageId, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<Mission>> EnumerateByVesselAsync(string tenantId, string vesselId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            return await EnumerateByTenantColumnAsync(tenantId, "vessel_id", vesselId, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<Mission>> EnumerateByCaptainAsync(string tenantId, string captainId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));
            return await EnumerateByTenantColumnAsync(tenantId, "captain_id", captainId, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<Mission>> EnumerateByStatusAsync(string tenantId, MissionStatusEnum status, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            return await EnumerateByTenantColumnAsync(tenantId, "status", status.ToString(), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT COUNT(*) FROM missions WHERE tenant_id = @tenantId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    long count = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                    return count > 0;
                }
            }
        }

        /// <inheritdoc />
        public async Task<Mission?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM missions WHERE tenant_id = @tenantId AND user_id = @userId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@userId", userId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return MissionColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
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
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM missions WHERE tenant_id = @tenantId AND user_id = @userId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@userId", userId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<Mission>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            List<Mission> results = new List<Mission>();
            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM missions WHERE tenant_id = @tenantId AND user_id = @userId ORDER BY created_utc DESC;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@userId", userId);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MissionColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }
            return results;
        }

        /// <inheritdoc />
        public Task<EnumerationResult<Mission>> EnumerateAsync(string tenantId, string userId, EnumerationQuery query, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            return EnumerateMissionRowsAsync("*", tenantId, userId, query, token);
        }

        #endregion

        #region Private-Methods

        private static void AddMissionParameters(NpgsqlCommand cmd, Mission mission)
        {
            MissionColumns.Write(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "missions"), mission);
        }

        private static async Task TouchVoyageAsync(NpgsqlConnection conn, string? voyageId, DateTime lastUpdateUtc, CancellationToken token)
        {
            if (String.IsNullOrEmpty(voyageId)) return;

            using (NpgsqlCommand cmd = new NpgsqlCommand())
            {
                cmd.Connection = conn;
                cmd.CommandText = "UPDATE voyages SET last_update_utc = @last_update_utc WHERE id = @voyage_id;";
                StoredValueBinder.Value(cmd, "@voyage_id", voyageId);
                PostgresqlDatabaseDriver.StoredBinder.For(cmd, "missions").Utc("@last_update_utc", "last_update_utc", lastUpdateUtc);
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        private async Task<List<Mission>> EnumerateByColumnAsync(string column, string value, CancellationToken token)
        {
            List<Mission> results = new List<Mission>();

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM missions WHERE " + column + " = @value ORDER BY created_utc DESC;";
                    StoredValueBinder.Value(cmd, "@value", value);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MissionColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        private async Task<List<Mission>> EnumerateByTenantColumnAsync(string tenantId, string column, string value, CancellationToken token)
        {
            List<Mission> results = new List<Mission>();

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM missions WHERE tenant_id = @tenantId AND " + column + " = @value ORDER BY created_utc DESC;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@value", value);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MissionColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <summary>Serialize prestaged files for storage. Null on empty.</summary>
        internal static string? SerializePrestagedFiles(List<PrestagedFile>? entries)
        {
            if (entries == null || entries.Count == 0) return null;
            return JsonSerializer.Serialize(entries);
        }

        #endregion
    }
}
