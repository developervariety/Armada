namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// PostgreSQL implementation of planning session persistence.
    /// </summary>
    public class PlanningSessionMethods : IPlanningSessionMethods
    {
        #region Private-Members

        private readonly PostgresqlDatabaseDriver _Driver;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="driver">Database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        public PlanningSessionMethods(PostgresqlDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<PlanningSession> CreateAsync(PlanningSession session, CancellationToken token = default)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            session.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO planning_sessions
                        (id, tenant_id, user_id, captain_id, vessel_id, fleet_id, dock_id, branch_name, title, status, pipeline_id, objective_id, selected_playbooks_json, process_id, failure_reason, created_utc, started_utc, completed_utc, last_update_utc)
                        VALUES
                        (@id, @tenant_id, @user_id, @captain_id, @vessel_id, @fleet_id, @dock_id, @branch_name, @title, @status, @pipeline_id, @objective_id, @selected_playbooks_json, @process_id, @failure_reason, @created_utc, @started_utc, @completed_utc, @last_update_utc);";
                    Bind(cmd, session);
                    cmd.Parameters.AddWithValue("@created_utc", MemoryRows.AsUtc(session.CreatedUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return session;
        }

        /// <inheritdoc />
        public async Task<PlanningSession?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            return await ReadInternalAsync(
                "SELECT * FROM planning_sessions WHERE id = @id;",
                cmd => cmd.Parameters.AddWithValue("@id", id),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<PlanningSession> UpdateAsync(PlanningSession session, CancellationToken token = default)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            session.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE planning_sessions SET
                        tenant_id = @tenant_id,
                        user_id = @user_id,
                        captain_id = @captain_id,
                        vessel_id = @vessel_id,
                        fleet_id = @fleet_id,
                        dock_id = @dock_id,
                        branch_name = @branch_name,
                        title = @title,
                        status = @status,
                        pipeline_id = @pipeline_id,
                        objective_id = @objective_id,
                        selected_playbooks_json = @selected_playbooks_json,
                        process_id = @process_id,
                        failure_reason = @failure_reason,
                        started_utc = @started_utc,
                        completed_utc = @completed_utc,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    Bind(cmd, session);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return session;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM planning_sessions WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<PlanningSession>> EnumerateAsync(CancellationToken token = default)
        {
            return await EnumerateInternalAsync("SELECT * FROM planning_sessions ORDER BY last_update_utc DESC;", null, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<PlanningSession>> EnumerateByCaptainAsync(string captainId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));
            return await EnumerateInternalAsync(
                "SELECT * FROM planning_sessions WHERE captain_id = @captain_id ORDER BY last_update_utc DESC;",
                cmd => cmd.Parameters.AddWithValue("@captain_id", captainId),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<PlanningSession>> EnumerateByStatusAsync(PlanningSessionStatusEnum status, CancellationToken token = default)
        {
            return await EnumerateInternalAsync(
                "SELECT * FROM planning_sessions WHERE status = @status ORDER BY last_update_utc DESC;",
                cmd => cmd.Parameters.AddWithValue("@status", status.ToString()),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<PlanningSession?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            return await ReadInternalAsync(
                "SELECT * FROM planning_sessions WHERE tenant_id = @tenant_id AND id = @id;",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                    cmd.Parameters.AddWithValue("@id", id);
                },
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<PlanningSession>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            return await EnumerateInternalAsync(
                "SELECT * FROM planning_sessions WHERE tenant_id = @tenant_id ORDER BY last_update_utc DESC;",
                cmd => cmd.Parameters.AddWithValue("@tenant_id", tenantId),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<PlanningSession?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            return await ReadInternalAsync(
                "SELECT * FROM planning_sessions WHERE tenant_id = @tenant_id AND user_id = @user_id AND id = @id;",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                    cmd.Parameters.AddWithValue("@user_id", userId);
                    cmd.Parameters.AddWithValue("@id", id);
                },
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<PlanningSession>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrEmpty(userId)) throw new ArgumentNullException(nameof(userId));
            return await EnumerateInternalAsync(
                "SELECT * FROM planning_sessions WHERE tenant_id = @tenant_id AND user_id = @user_id ORDER BY last_update_utc DESC;",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@tenant_id", tenantId);
                    cmd.Parameters.AddWithValue("@user_id", userId);
                },
                token).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        private async Task<PlanningSession?> ReadInternalAsync(string sql, Action<NpgsqlCommand> parameterize, CancellationToken token)
        {
            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    parameterize(cmd);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return FromReader(reader);
                    }
                }
            }

            return null;
        }

        private async Task<List<PlanningSession>> EnumerateInternalAsync(string sql, Action<NpgsqlCommand>? parameterize, CancellationToken token)
        {
            List<PlanningSession> results = new List<PlanningSession>();
            using (NpgsqlConnection conn = _Driver.CreateConnection())
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    parameterize?.Invoke(cmd);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(FromReader(reader));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Bind every column an update writes. The creation time is bound only by an insert.
        /// </summary>
        private static void Bind(NpgsqlCommand cmd, PlanningSession session)
        {
            cmd.Parameters.AddWithValue("@id", session.Id);
            cmd.Parameters.AddWithValue("@tenant_id", (object?)session.TenantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@user_id", (object?)session.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@captain_id", session.CaptainId);
            cmd.Parameters.AddWithValue("@vessel_id", session.VesselId);
            cmd.Parameters.AddWithValue("@fleet_id", (object?)session.FleetId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@dock_id", (object?)session.DockId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@branch_name", (object?)session.BranchName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@title", session.Title);
            cmd.Parameters.AddWithValue("@status", session.Status.ToString());
            cmd.Parameters.AddWithValue("@pipeline_id", (object?)session.PipelineId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@objective_id", (object?)session.ObjectiveId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@selected_playbooks_json", session.SerializeSelectedPlaybooks());
            cmd.Parameters.AddWithValue("@process_id", session.ProcessId.HasValue ? (object)session.ProcessId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@failure_reason", (object?)session.FailureReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@started_utc", session.StartedUtc.HasValue ? (object)MemoryRows.AsUtc(session.StartedUtc.Value) : DBNull.Value);
            cmd.Parameters.AddWithValue("@completed_utc", session.CompletedUtc.HasValue ? (object)MemoryRows.AsUtc(session.CompletedUtc.Value) : DBNull.Value);
            cmd.Parameters.AddWithValue("@last_update_utc", MemoryRows.AsUtc(session.LastUpdateUtc));
        }

        private static PlanningSession FromReader(NpgsqlDataReader reader)
        {
            PlanningSession session = new PlanningSession
            {
                Id = reader["id"].ToString()!,
                TenantId = NullableString(reader["tenant_id"]),
                UserId = NullableString(reader["user_id"]),
                CaptainId = reader["captain_id"].ToString()!,
                VesselId = reader["vessel_id"].ToString()!,
                FleetId = NullableString(reader["fleet_id"]),
                DockId = NullableString(reader["dock_id"]),
                BranchName = NullableString(reader["branch_name"]),
                Title = reader["title"].ToString()!,
                Status = Enum.Parse<PlanningSessionStatusEnum>(reader["status"].ToString()!),
                PipelineId = NullableString(reader["pipeline_id"]),
                ObjectiveId = NullableString(reader["objective_id"]),
                ProcessId = reader["process_id"] == DBNull.Value ? null : Convert.ToInt32(reader["process_id"]),
                FailureReason = NullableString(reader["failure_reason"]),
                CreatedUtc = PostgresqlDatabaseDriver.ReadUtc(reader["created_utc"]),
                StartedUtc = PostgresqlDatabaseDriver.ReadUtcNullable(reader["started_utc"]),
                CompletedUtc = PostgresqlDatabaseDriver.ReadUtcNullable(reader["completed_utc"]),
                LastUpdateUtc = PostgresqlDatabaseDriver.ReadUtc(reader["last_update_utc"])
            };
            session.DeserializeSelectedPlaybooks(NullableString(reader["selected_playbooks_json"]));
            return session;
        }

        private static string? NullableString(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            string str = value.ToString()!;
            return String.IsNullOrEmpty(str) ? null : str;
        }

        #endregion
    }
}
