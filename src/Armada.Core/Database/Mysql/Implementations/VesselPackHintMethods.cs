namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// MySQL implementation of vessel pack-curate hint persistence (v2-F1).
    /// </summary>
    public class VesselPackHintMethods : IVesselPackHintMethods
    {
        #region Private-Members

        private readonly string _ConnectionString;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        public VesselPackHintMethods(string connectionString, DatabaseSettings settings, LoggingModule logging)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<VesselPackHint> CreateAsync(VesselPackHint hint, CancellationToken token = default)
        {
            if (hint == null) throw new ArgumentNullException(nameof(hint));
            hint.LastUpdateUtc = DateTime.UtcNow;
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO vessel_pack_hints
                        (id, vessel_id, goal_pattern, must_include, must_exclude, priority, confidence,
                         source_mission_ids, justification, active, created_utc, last_update_utc)
                        VALUES (@id, @vessel_id, @goal_pattern, @must_include, @must_exclude, @priority, @confidence,
                         @source_mission_ids, @justification, @active, @created_utc, @last_update_utc);";
                    AddParameters(cmd, hint);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return hint;
        }

        /// <inheritdoc />
        public async Task<VesselPackHint?> ReadAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM vessel_pack_hints WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (MySqlDataReader reader = (MySqlDataReader)await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return FromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<VesselPackHint> UpdateAsync(VesselPackHint hint, CancellationToken token = default)
        {
            if (hint == null) throw new ArgumentNullException(nameof(hint));
            hint.LastUpdateUtc = DateTime.UtcNow;
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE vessel_pack_hints SET
                        vessel_id = @vessel_id,
                        goal_pattern = @goal_pattern,
                        must_include = @must_include,
                        must_exclude = @must_exclude,
                        priority = @priority,
                        confidence = @confidence,
                        source_mission_ids = @source_mission_ids,
                        justification = @justification,
                        active = @active,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    AddParameters(cmd, hint);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return hint;
        }

        /// <inheritdoc />
        public async Task DeactivateAsync(string id, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE vessel_pack_hints SET active = 0, last_update_utc = @last_update_utc WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@last_update_utc", DateTime.UtcNow);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
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
                    cmd.CommandText = "DELETE FROM vessel_pack_hints WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public Task<List<VesselPackHint>> EnumerateByVesselAsync(string vesselId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            return EnumerateInternalAsync(vesselId, false, token);
        }

        /// <inheritdoc />
        public Task<List<VesselPackHint>> EnumerateActiveByVesselAsync(string vesselId, CancellationToken token = default)
        {
            if (String.IsNullOrEmpty(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            return EnumerateInternalAsync(vesselId, true, token);
        }

        #endregion

        #region Private-Methods

        private async Task<List<VesselPackHint>> EnumerateInternalAsync(string vesselId, bool activeOnly, CancellationToken token)
        {
            List<VesselPackHint> results = new List<VesselPackHint>();
            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = activeOnly
                        ? "SELECT * FROM vessel_pack_hints WHERE vessel_id = @vessel_id AND active = 1 ORDER BY priority DESC, created_utc ASC;"
                        : "SELECT * FROM vessel_pack_hints WHERE vessel_id = @vessel_id ORDER BY priority DESC, created_utc ASC;";
                    cmd.Parameters.AddWithValue("@vessel_id", vesselId);
                    using (MySqlDataReader reader = (MySqlDataReader)await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(FromReader(reader));
                    }
                }
            }

            return results;
        }

        private static void AddParameters(MySqlCommand cmd, VesselPackHint hint)
        {
            cmd.Parameters.AddWithValue("@id", hint.Id);
            cmd.Parameters.AddWithValue("@vessel_id", hint.VesselId);
            cmd.Parameters.AddWithValue("@goal_pattern", hint.GoalPattern);
            cmd.Parameters.AddWithValue("@must_include", hint.MustIncludeJson ?? "[]");
            cmd.Parameters.AddWithValue("@must_exclude", hint.MustExcludeJson ?? "[]");
            cmd.Parameters.AddWithValue("@priority", hint.Priority);
            cmd.Parameters.AddWithValue("@confidence", hint.Confidence ?? "medium");
            cmd.Parameters.AddWithValue("@source_mission_ids", hint.SourceMissionIdsJson ?? "[]");
            cmd.Parameters.AddWithValue("@justification", (object?)hint.Justification ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@active", hint.Active ? 1 : 0);
            cmd.Parameters.AddWithValue("@created_utc", hint.CreatedUtc);
            cmd.Parameters.AddWithValue("@last_update_utc", hint.LastUpdateUtc);
        }

        private static VesselPackHint FromReader(MySqlDataReader reader)
        {
            VesselPackHint hint = new VesselPackHint();
            hint.Id = reader["id"].ToString()!;
            hint.VesselId = reader["vessel_id"].ToString()!;
            hint.GoalPattern = reader["goal_pattern"].ToString()!;
            hint.MustIncludeJson = reader["must_include"].ToString() ?? "[]";
            hint.MustExcludeJson = reader["must_exclude"].ToString() ?? "[]";
            hint.Priority = Convert.ToInt32(reader["priority"]);
            hint.Confidence = reader["confidence"].ToString() ?? "medium";
            hint.SourceMissionIdsJson = reader["source_mission_ids"].ToString() ?? "[]";
            hint.Justification = reader["justification"] == DBNull.Value ? null : reader["justification"].ToString();
            hint.Active = Convert.ToInt64(reader["active"]) == 1;
            hint.CreatedUtc = Convert.ToDateTime(reader["created_utc"]);
            hint.LastUpdateUtc = Convert.ToDateTime(reader["last_update_utc"]);
            return hint;
        }

        #endregion
    }
}
