namespace Armada.Core.Database.Sqlite.Implementations
{
    using System.Text.Json;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// SQLite implementation of normalized objective persistence.
    /// </summary>
    public class ObjectiveMethods : IObjectiveMethods
    {
        private readonly SqliteDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectiveMethods"/> class.
        /// </summary>
        public ObjectiveMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc />
        public async Task<Objective> CreateAsync(Objective objective, CancellationToken token = default)
        {
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO objectives
                        (id, tenant_id, user_id, title, description, status, kind, category, priority, rank, auto_dispatch_enabled, start_from_ref, backlog_state, effort, owner, target_version, due_utc, parent_objective_id, blocked_by_objective_ids_json, refinement_summary, preparation_json, suggested_pipeline_id, suggested_playbooks_json, tags_json, acceptance_criteria_json, non_goals_json, rollout_constraints_json, evidence_links_json, fleet_ids_json, vessel_ids_json, planning_session_ids_json, refinement_session_ids_json, voyage_ids_json, mission_ids_json, check_run_ids_json, release_ids_json, deployment_ids_json, incident_ids_json, source_provider, source_type, source_id, source_url, source_updated_utc, created_utc, last_update_utc, completed_utc)
                        VALUES
                        (@id, @tenant_id, @user_id, @title, @description, @status, @kind, @category, @priority, @rank, @auto_dispatch_enabled, @start_from_ref, @backlog_state, @effort, @owner, @target_version, @due_utc, @parent_objective_id, @blocked_by_objective_ids_json, @refinement_summary, @preparation_json, @suggested_pipeline_id, @suggested_playbooks_json, @tags_json, @acceptance_criteria_json, @non_goals_json, @rollout_constraints_json, @evidence_links_json, @fleet_ids_json, @vessel_ids_json, @planning_session_ids_json, @refinement_session_ids_json, @voyage_ids_json, @mission_ids_json, @check_run_ids_json, @release_ids_json, @deployment_ids_json, @incident_ids_json, @source_provider, @source_type, @source_id, @source_url, @source_updated_utc, @created_utc, @last_update_utc, @completed_utc);";
                    BindObjective(cmd, objective);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return objective;
        }

        /// <inheritdoc />
        public async Task<Objective> UpdateAsync(Objective objective, CancellationToken token = default)
        {
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE objectives SET
                        tenant_id = @tenant_id,
                        user_id = @user_id,
                        title = @title,
                        description = @description,
                        status = @status,
                        kind = @kind,
                        category = @category,
                        priority = @priority,
                        rank = @rank,
                        auto_dispatch_enabled = @auto_dispatch_enabled,
                        start_from_ref = @start_from_ref,
                        backlog_state = @backlog_state,
                        effort = @effort,
                        owner = @owner,
                        target_version = @target_version,
                        due_utc = @due_utc,
                        parent_objective_id = @parent_objective_id,
                        blocked_by_objective_ids_json = @blocked_by_objective_ids_json,
                        refinement_summary = @refinement_summary,
                        preparation_json = @preparation_json,
                        suggested_pipeline_id = @suggested_pipeline_id,
                        suggested_playbooks_json = @suggested_playbooks_json,
                        tags_json = @tags_json,
                        acceptance_criteria_json = @acceptance_criteria_json,
                        non_goals_json = @non_goals_json,
                        rollout_constraints_json = @rollout_constraints_json,
                        evidence_links_json = @evidence_links_json,
                        fleet_ids_json = @fleet_ids_json,
                        vessel_ids_json = @vessel_ids_json,
                        planning_session_ids_json = @planning_session_ids_json,
                        refinement_session_ids_json = @refinement_session_ids_json,
                        voyage_ids_json = @voyage_ids_json,
                        mission_ids_json = @mission_ids_json,
                        check_run_ids_json = @check_run_ids_json,
                        release_ids_json = @release_ids_json,
                        deployment_ids_json = @deployment_ids_json,
                        incident_ids_json = @incident_ids_json,
                        source_provider = @source_provider,
                        source_type = @source_type,
                        source_id = @source_id,
                        source_url = @source_url,
                        source_updated_utc = @source_updated_utc,
                        created_utc = @created_utc,
                        last_update_utc = @last_update_utc,
                        completed_utc = @completed_utc
                        WHERE id = @id;";
                    BindObjective(cmd, objective);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return objective;
        }

        /// <inheritdoc />
        public async Task<Objective?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return await ReadInternalAsync("SELECT * FROM objectives WHERE id = @id;", cmd => StoredValueBinder.Value(cmd, "@id", id), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Objective?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return await ReadInternalAsync("SELECT * FROM objectives WHERE tenant_id = @tenant_id AND id = @id;", cmd =>
            {
                StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                StoredValueBinder.Value(cmd, "@id", id);
            }, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<Objective?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(userId)) throw new ArgumentNullException(nameof(userId));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return await ReadInternalAsync("SELECT * FROM objectives WHERE tenant_id = @tenant_id AND user_id = @user_id AND id = @id;", cmd =>
            {
                StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                StoredValueBinder.Value(cmd, "@user_id", userId);
                StoredValueBinder.Value(cmd, "@id", id);
            }, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            await ExecuteDeleteAsync("DELETE FROM objectives WHERE id = @id;", cmd => StoredValueBinder.Value(cmd, "@id", id), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            await ExecuteDeleteAsync("DELETE FROM objectives WHERE tenant_id = @tenant_id AND id = @id;", cmd =>
            {
                StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                StoredValueBinder.Value(cmd, "@id", id);
            }, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<Objective>> EnumerateAsync(CancellationToken token = default)
        {
            return await EnumerateInternalAsync("SELECT * FROM objectives ORDER BY rank ASC, priority ASC, last_update_utc DESC;", null, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<Objective>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            return await EnumerateInternalAsync("SELECT * FROM objectives WHERE tenant_id = @tenant_id ORDER BY rank ASC, priority ASC, last_update_utc DESC;", cmd => StoredValueBinder.Value(cmd, "@tenant_id", tenantId), token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<Objective>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(userId)) throw new ArgumentNullException(nameof(userId));
            return await EnumerateInternalAsync("SELECT * FROM objectives WHERE tenant_id = @tenant_id AND user_id = @user_id ORDER BY rank ASC, priority ASC, last_update_utc DESC;", cmd =>
            {
                StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                StoredValueBinder.Value(cmd, "@user_id", userId);
            }, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAnyAsync(CancellationToken token = default)
        {
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT 1 FROM objectives LIMIT 1;";
                    object? result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
                    return result != null && result != DBNull.Value;
                }
            }
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM objectives WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    long count = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0L);
                    return count > 0;
                }
            }
        }

        private async Task<Objective?> ReadInternalAsync(string sql, Action<SqliteCommand> parameterize, CancellationToken token)
        {
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    parameterize(cmd);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return ObjectiveColumns.Read(reader, SqliteDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        private async Task<List<Objective>> EnumerateInternalAsync(string sql, Action<SqliteCommand>? parameterize, CancellationToken token)
        {
            List<Objective> results = new List<Objective>();
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    parameterize?.Invoke(cmd);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        results.AddRange(await ObjectivePersistenceHelper.ReadRowsAsync(
                            reader, () => ObjectiveColumns.Read(reader, SqliteDatabaseDriver.StoredValues), _Logging, token).ConfigureAwait(false));
                    }
                }
            }

            return results;
        }

        private async Task ExecuteDeleteAsync(string sql, Action<SqliteCommand> parameterize, CancellationToken token)
        {
            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    parameterize(cmd);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        private static void BindObjective(SqliteCommand cmd, Objective objective)
        {
            objective.NormalizeTenancy();

            ObjectiveColumns.Write(SqliteDatabaseDriver.StoredBinder.For(cmd, "objectives"), objective);
        }
    }
}
