namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// PostgreSQL implementation of coordination message database operations.
    /// </summary>
    public class CoordinationMessageMethods : ICoordinationMessageMethods
    {
        #region Private-Members

        private readonly NpgsqlDataSource _DataSource;
        private static readonly string _Iso8601Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate the PostgreSQL coordination message methods.
        /// </summary>
        /// <param name="dataSource">NpgsqlDataSource instance.</param>
        public CoordinationMessageMethods(NpgsqlDataSource dataSource)
        {
            _DataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<CoordinationMessage> CreateAsync(CoordinationMessage message, CancellationToken token = default)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            message.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"INSERT INTO coordination_messages
                        (id, coordination_room_id, tenant_id, author_type, author_id, author_name, content,
                         voyage_id, mission_id, vessel_id, incident_id, created_utc, last_update_utc)
                        VALUES
                        (@id, @coordination_room_id, @tenant_id, @author_type, @author_id, @author_name, @content,
                         @voyage_id, @mission_id, @vessel_id, @incident_id, @created_utc, @last_update_utc);";
                    cmd.Parameters.AddWithValue("@id", message.Id);
                    cmd.Parameters.AddWithValue("@coordination_room_id", message.CoordinationRoomId);
                    cmd.Parameters.AddWithValue("@tenant_id", (object?)message.TenantId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@author_type", message.AuthorType.ToString());
                    cmd.Parameters.AddWithValue("@author_id", (object?)message.AuthorId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@author_name", message.AuthorName);
                    cmd.Parameters.AddWithValue("@content", message.Content);
                    cmd.Parameters.AddWithValue("@voyage_id", (object?)message.VoyageId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@mission_id", (object?)message.MissionId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@vessel_id", (object?)message.VesselId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@incident_id", (object?)message.IncidentId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@created_utc", ToIso8601(message.CreatedUtc));
                    cmd.Parameters.AddWithValue("@last_update_utc", ToIso8601(message.LastUpdateUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return message;
        }

        /// <inheritdoc />
        public async Task<CoordinationMessage?> ReadAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM coordination_messages WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return MessageFromReader(reader);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<CoordinationMessage> UpdateAsync(CoordinationMessage message, CancellationToken token = default)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            message.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"UPDATE coordination_messages SET
                        coordination_room_id = @coordination_room_id,
                        tenant_id = @tenant_id,
                        author_type = @author_type,
                        author_id = @author_id,
                        author_name = @author_name,
                        content = @content,
                        voyage_id = @voyage_id,
                        mission_id = @mission_id,
                        vessel_id = @vessel_id,
                        incident_id = @incident_id,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", message.Id);
                    cmd.Parameters.AddWithValue("@coordination_room_id", message.CoordinationRoomId);
                    cmd.Parameters.AddWithValue("@tenant_id", (object?)message.TenantId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@author_type", message.AuthorType.ToString());
                    cmd.Parameters.AddWithValue("@author_id", (object?)message.AuthorId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@author_name", message.AuthorName);
                    cmd.Parameters.AddWithValue("@content", message.Content);
                    cmd.Parameters.AddWithValue("@voyage_id", (object?)message.VoyageId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@mission_id", (object?)message.MissionId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@vessel_id", (object?)message.VesselId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@incident_id", (object?)message.IncidentId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@last_update_utc", ToIso8601(message.LastUpdateUtc));
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return message;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM coordination_messages WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task DeleteByRoomAsync(string coordinationRoomId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM coordination_messages WHERE coordination_room_id = @coordination_room_id;";
                    cmd.Parameters.AddWithValue("@coordination_room_id", coordinationRoomId);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<CoordinationMessage>> EnumerateByRoomAsync(string coordinationRoomId, DateTime? afterUtc = null, int limit = 200, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(coordinationRoomId)) throw new ArgumentNullException(nameof(coordinationRoomId));
            if (limit < 1) limit = 1;
            if (limit > 1000) limit = 1000;

            List<CoordinationMessage> results = new List<CoordinationMessage>();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    if (afterUtc.HasValue)
                    {
                        cmd.CommandText = "SELECT * FROM coordination_messages WHERE coordination_room_id = @coordination_room_id AND created_utc > @after_utc ORDER BY created_utc ASC LIMIT @limit;";
                        cmd.Parameters.AddWithValue("@after_utc", ToIso8601(afterUtc.Value));
                    }
                    else
                    {
                        cmd.CommandText = "SELECT * FROM coordination_messages WHERE coordination_room_id = @coordination_room_id ORDER BY created_utc DESC LIMIT @limit;";
                    }

                    cmd.Parameters.AddWithValue("@coordination_room_id", coordinationRoomId);
                    cmd.Parameters.AddWithValue("@limit", limit);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(MessageFromReader(reader));
                    }
                }
            }

            if (!afterUtc.HasValue) results.Reverse();
            return results;
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

        private static string? NullableString(object value)
        {
            if (value == null || value == DBNull.Value) return null;
            string str = value.ToString()!;
            return string.IsNullOrEmpty(str) ? null : str;
        }

        private static CoordinationMessage MessageFromReader(NpgsqlDataReader reader)
        {
            CoordinationMessage message = new CoordinationMessage();
            message.Id = reader["id"].ToString()!;
            message.CoordinationRoomId = reader["coordination_room_id"].ToString()!;
            message.TenantId = NullableString(reader["tenant_id"]);
            message.AuthorType = Enum.Parse<CoordinationAuthorTypeEnum>(reader["author_type"].ToString()!);
            message.AuthorId = NullableString(reader["author_id"]);
            message.AuthorName = reader["author_name"].ToString()!;
            message.Content = NullableString(reader["content"]) ?? String.Empty;
            message.VoyageId = NullableString(reader["voyage_id"]);
            message.MissionId = NullableString(reader["mission_id"]);
            message.VesselId = NullableString(reader["vessel_id"]);
            message.IncidentId = NullableString(reader["incident_id"]);
            message.CreatedUtc = FromIso8601(reader["created_utc"].ToString()!);
            message.LastUpdateUtc = FromIso8601(reader["last_update_utc"].ToString()!);
            return message;
        }

        #endregion
    }
}
