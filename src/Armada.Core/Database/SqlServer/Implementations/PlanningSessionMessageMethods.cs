namespace Armada.Core.Database.SqlServer.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// SQL Server implementation of planning session transcript persistence.
    /// </summary>
    public class PlanningSessionMessageMethods : IPlanningSessionMessageMethods
    {
        #region Private-Members

        private readonly SqlServerDatabaseDriver _Driver;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="driver">Database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        public PlanningSessionMessageMethods(SqlServerDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<PlanningSessionMessage> CreateAsync(PlanningSessionMessage message, CancellationToken token = default)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            message.LastUpdateUtc = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO planning_session_messages
                        (id, planning_session_id, tenant_id, user_id, role, sequence, content, is_selected_for_dispatch, created_utc, last_update_utc)
                        VALUES
                        (@id, @planning_session_id, @tenant_id, @user_id, @role, @sequence, @content, @is_selected_for_dispatch, @created_utc, @last_update_utc);";
                    Bind(cmd, message);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return message;
        }

        /// <inheritdoc />
        public async Task<PlanningSessionMessage?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM planning_session_messages WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return PlanningSessionMessageColumns.Read(reader, SqlServerDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<PlanningSessionMessage> UpdateAsync(PlanningSessionMessage message, CancellationToken token = default)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            message.LastUpdateUtc = DateTime.UtcNow;

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE planning_session_messages SET
                        planning_session_id = @planning_session_id,
                        tenant_id = @tenant_id,
                        user_id = @user_id,
                        role = @role,
                        sequence = @sequence,
                        content = @content,
                        is_selected_for_dispatch = @is_selected_for_dispatch,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    Bind(cmd, message);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return message;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM planning_session_messages WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task DeleteBySessionAsync(string planningSessionId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(planningSessionId)) throw new ArgumentNullException(nameof(planningSessionId));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM planning_session_messages WHERE planning_session_id = @planning_session_id;";
                    StoredValueBinder.Value(cmd, "@planning_session_id", planningSessionId);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<PlanningSessionMessage>> EnumerateBySessionAsync(string planningSessionId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(planningSessionId)) throw new ArgumentNullException(nameof(planningSessionId));

            List<PlanningSessionMessage> results = new List<PlanningSessionMessage>();
            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM planning_session_messages WHERE planning_session_id = @planning_session_id ORDER BY sequence ASC, created_utc ASC;";
                    StoredValueBinder.Value(cmd, "@planning_session_id", planningSessionId);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(PlanningSessionMessageColumns.Read(reader, SqlServerDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Bind every stored column; an update statement leaves the creation time as it is.
        /// </summary>
        private static void Bind(SqlCommand cmd, PlanningSessionMessage message)
        {
            PlanningSessionMessageColumns.Write(SqlServerDatabaseDriver.StoredBinder.For(cmd, "planning_session_messages"), message);
        }

        #endregion
    }
}
