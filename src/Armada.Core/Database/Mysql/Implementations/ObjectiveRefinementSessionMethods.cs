namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// MySQL objective refinement session persistence.
    /// </summary>
    public class ObjectiveRefinementSessionMethods : IObjectiveRefinementSessionMethods
    {
        private readonly string _ConnectionString;
        private readonly SyslogLogging.LoggingModule _Logging;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObjectiveRefinementSessionMethods"/> class.
        /// </summary>
        /// <param name="connectionString">MySQL connection string.</param>
        /// <param name="logging">Logging module that records skipped unreadable rows.</param>
        public ObjectiveRefinementSessionMethods(string connectionString, SyslogLogging.LoggingModule logging)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc />
        public async Task<ObjectiveRefinementSession> CreateAsync(ObjectiveRefinementSession session, CancellationToken token = default)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO objective_refinement_sessions
                        (id, objective_id, tenant_id, user_id, captain_id, fleet_id, vessel_id, title, status, process_id, failure_reason, created_utc, started_utc, completed_utc, last_update_utc)
                        VALUES
                        (@id, @objective_id, @tenant_id, @user_id, @captain_id, @fleet_id, @vessel_id, @title, @status, @process_id, @failure_reason, @created_utc, @started_utc, @completed_utc, @last_update_utc);";
                    Bind(cmd, session);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return session;
        }

        /// <inheritdoc />
        public async Task<ObjectiveRefinementSession> UpdateAsync(ObjectiveRefinementSession session, CancellationToken token = default)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE objective_refinement_sessions SET
                        objective_id = @objective_id,
                        tenant_id = @tenant_id,
                        user_id = @user_id,
                        captain_id = @captain_id,
                        fleet_id = @fleet_id,
                        vessel_id = @vessel_id,
                        title = @title,
                        status = @status,
                        process_id = @process_id,
                        failure_reason = @failure_reason,
                        created_utc = @created_utc,
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
        public async Task<ObjectiveRefinementSession?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return await ReadInternalAsync(
                "SELECT * FROM objective_refinement_sessions WHERE id = @id;",
                cmd => StoredValueBinder.Value(cmd, "@id", id),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<ObjectiveRefinementSession?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return await ReadInternalAsync(
                "SELECT * FROM objective_refinement_sessions WHERE tenant_id = @tenant_id AND id = @id;",
                cmd =>
                {
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                },
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<ObjectiveRefinementSession?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(userId)) throw new ArgumentNullException(nameof(userId));
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            return await ReadInternalAsync(
                "SELECT * FROM objective_refinement_sessions WHERE tenant_id = @tenant_id AND user_id = @user_id AND id = @id;",
                cmd =>
                {
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@user_id", userId);
                    StoredValueBinder.Value(cmd, "@id", id);
                },
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM objective_refinement_sessions WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<ObjectiveRefinementSession>> EnumerateAsync(CancellationToken token = default)
        {
            return await EnumerateInternalAsync("SELECT * FROM objective_refinement_sessions ORDER BY last_update_utc DESC;", null, token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<ObjectiveRefinementSession>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            return await EnumerateInternalAsync(
                "SELECT * FROM objective_refinement_sessions WHERE tenant_id = @tenant_id ORDER BY last_update_utc DESC;",
                cmd => StoredValueBinder.Value(cmd, "@tenant_id", tenantId),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<ObjectiveRefinementSession>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (String.IsNullOrWhiteSpace(userId)) throw new ArgumentNullException(nameof(userId));
            return await EnumerateInternalAsync(
                "SELECT * FROM objective_refinement_sessions WHERE tenant_id = @tenant_id AND user_id = @user_id ORDER BY last_update_utc DESC;",
                cmd =>
                {
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@user_id", userId);
                },
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<ObjectiveRefinementSession>> EnumerateByObjectiveAsync(string objectiveId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(objectiveId)) throw new ArgumentNullException(nameof(objectiveId));
            return await EnumerateInternalAsync(
                "SELECT * FROM objective_refinement_sessions WHERE objective_id = @objective_id ORDER BY created_utc DESC;",
                cmd => StoredValueBinder.Value(cmd, "@objective_id", objectiveId),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<ObjectiveRefinementSession>> EnumerateByCaptainAsync(string captainId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(captainId)) throw new ArgumentNullException(nameof(captainId));
            return await EnumerateInternalAsync(
                "SELECT * FROM objective_refinement_sessions WHERE captain_id = @captain_id ORDER BY last_update_utc DESC;",
                cmd => StoredValueBinder.Value(cmd, "@captain_id", captainId),
                token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<List<ObjectiveRefinementSession>> EnumerateByStatusAsync(ObjectiveRefinementSessionStatusEnum status, CancellationToken token = default)
        {
            return await EnumerateInternalAsync(
                "SELECT * FROM objective_refinement_sessions WHERE status = @status ORDER BY last_update_utc DESC;",
                cmd => StoredValueBinder.Value(cmd, "@status", status.ToString()),
                token).ConfigureAwait(false);
        }

        private async Task<ObjectiveRefinementSession?> ReadInternalAsync(string sql, Action<MySqlCommand> parameterize, CancellationToken token)
        {
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    parameterize(cmd);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return ObjectiveRefinementSessionColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        private async Task<List<ObjectiveRefinementSession>> EnumerateInternalAsync(string sql, Action<MySqlCommand>? parameterize, CancellationToken token)
        {
            List<ObjectiveRefinementSession> results = new List<ObjectiveRefinementSession>();
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    parameterize?.Invoke(cmd);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        results = await RefinementSessionPersistenceHelper.ReadRowsAsync(
                            reader, () => ObjectiveRefinementSessionColumns.Read(reader, MysqlDatabaseDriver.StoredValues), _Logging, token).ConfigureAwait(false);
                    }
                }
            }

            return results;
        }

        private static void Bind(MySqlCommand cmd, ObjectiveRefinementSession session)
        {
            ObjectiveRefinementSessionColumns.Write(MysqlDatabaseDriver.StoredBinder.For(cmd, "objective_refinement_sessions"), session);
        }
    }
}
