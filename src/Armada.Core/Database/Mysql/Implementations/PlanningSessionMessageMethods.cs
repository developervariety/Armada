namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// MySQL implementation of planning session transcript persistence.
    /// </summary>
    public class PlanningSessionMessageMethods : IPlanningSessionMessageMethods
    {
        #region Private-Members

        private readonly string _ConnectionString;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="connectionString">MySQL connection string.</param>
        public PlanningSessionMessageMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<PlanningSessionMessage> CreateAsync(PlanningSessionMessage message, CancellationToken token = default)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            message.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO planning_session_messages
                        (id, planning_session_id, tenant_id, user_id, role, sequence, content, is_selected_for_dispatch, created_utc, last_update_utc)
                        VALUES
                        (@id, @planning_session_id, @tenant_id, @user_id, @role, @sequence, @content, @is_selected_for_dispatch, @created_utc, @last_update_utc);";
                    Bind(cmd, message);
                    cmd.Parameters.AddWithValue("@created_utc", MemoryRows.AsUtc(message.CreatedUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return message;
        }

        /// <inheritdoc />
        public async Task<PlanningSessionMessage?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM planning_session_messages WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return FromReader(reader);
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

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
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

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM planning_session_messages WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task DeleteBySessionAsync(string planningSessionId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(planningSessionId)) throw new ArgumentNullException(nameof(planningSessionId));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM planning_session_messages WHERE planning_session_id = @planning_session_id;";
                    cmd.Parameters.AddWithValue("@planning_session_id", planningSessionId);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<PlanningSessionMessage>> EnumerateBySessionAsync(string planningSessionId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(planningSessionId)) throw new ArgumentNullException(nameof(planningSessionId));

            List<PlanningSessionMessage> results = new List<PlanningSessionMessage>();
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM planning_session_messages WHERE planning_session_id = @planning_session_id ORDER BY sequence ASC, created_utc ASC;";
                    cmd.Parameters.AddWithValue("@planning_session_id", planningSessionId);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(FromReader(reader));
                    }
                }
            }

            return results;
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Bind every column an update writes. The creation time is bound only by an insert.
        /// </summary>
        private static void Bind(MySqlCommand cmd, PlanningSessionMessage message)
        {
            cmd.Parameters.AddWithValue("@id", message.Id);
            cmd.Parameters.AddWithValue("@planning_session_id", message.PlanningSessionId);
            cmd.Parameters.AddWithValue("@tenant_id", (object?)message.TenantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@user_id", (object?)message.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@role", message.Role);
            cmd.Parameters.AddWithValue("@sequence", message.Sequence);
            cmd.Parameters.AddWithValue("@content", message.Content);
            cmd.Parameters.AddWithValue("@is_selected_for_dispatch", message.IsSelectedForDispatch);
            cmd.Parameters.AddWithValue("@last_update_utc", MemoryRows.AsUtc(message.LastUpdateUtc));
        }

        private static PlanningSessionMessage FromReader(MySqlDataReader reader)
        {
            return new PlanningSessionMessage
            {
                Id = reader["id"].ToString()!,
                PlanningSessionId = reader["planning_session_id"].ToString()!,
                TenantId = NullableString(reader["tenant_id"]),
                UserId = NullableString(reader["user_id"]),
                Role = reader["role"].ToString()!,
                Sequence = Convert.ToInt32(reader["sequence"]),
                Content = NullableString(reader["content"]) ?? String.Empty,
                IsSelectedForDispatch = Convert.ToBoolean(reader["is_selected_for_dispatch"]),
                CreatedUtc = MysqlDatabaseDriver.ReadUtc(reader["created_utc"]),
                LastUpdateUtc = MysqlDatabaseDriver.ReadUtc(reader["last_update_utc"])
            };
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
