namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// PostgreSQL implementation of user database operations.
    /// </summary>
    public class UserMethods : IUserMethods
    {
        #region Private-Members

#pragma warning disable CS0414
        private string _Header = "[UserMethods] ";
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
        public UserMethods(PostgresqlDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<UserMaster> CreateAsync(UserMaster user, CancellationToken token = default)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            user.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"INSERT INTO users (id, tenant_id, email, password_sha256, first_name, last_name, is_admin, is_tenant_admin, is_protected, active, created_utc, last_update_utc)
                        VALUES (@id, @tenant_id, @email, @password_sha256, @first_name, @last_name, @is_admin, @is_tenant_admin, @is_protected, @active, @created_utc, @last_update_utc);";
                    UserColumns.Write(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "users"), user);
                    StoredValueBinder.Value(cmd, "@password_sha256", user.PasswordSha256);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return user;
        }

        /// <inheritdoc />
        public async Task<UserMaster?> ReadAsync(string tenantId, string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM users WHERE tenant_id = @tenantId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return UserColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<UserMaster?> ReadByIdAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM users WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return UserColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<UserMaster?> ReadByEmailAsync(string tenantId, string email, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(email)) throw new ArgumentNullException(nameof(email));

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM users WHERE tenant_id = @tenantId AND email = @email;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@email", email);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return UserColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<List<UserMaster>> ReadByEmailAnyTenantAsync(string email, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(email)) throw new ArgumentNullException(nameof(email));

            List<UserMaster> results = new List<UserMaster>();

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM users WHERE email = @email;";
                    StoredValueBinder.Value(cmd, "@email", email);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(UserColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<UserMaster> UpdateAsync(UserMaster user, CancellationToken token = default)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            user.LastUpdateUtc = DateTime.UtcNow;

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"UPDATE users SET
                        tenant_id = @tenant_id,
                        email = @email,
                        password_sha256 = @password_sha256,
                            first_name = @first_name,
                            last_name = @last_name,
                            is_admin = @is_admin,
                            is_tenant_admin = @is_tenant_admin,
                            is_protected = @is_protected,
                        active = @active,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    UserColumns.Write(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "users"), user);
                    StoredValueBinder.Value(cmd, "@password_sha256", user.PasswordSha256);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return user;
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
                    cmd.CommandText = "DELETE FROM users WHERE tenant_id = @tenantId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<List<UserMaster>> EnumerateAsync(string tenantId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));

            List<UserMaster> results = new List<UserMaster>();

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM users WHERE tenant_id = @tenantId ORDER BY email;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(UserColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<UserMaster>> EnumerateAsync(string tenantId, EnumerationQuery query, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (query == null) query = new EnumerationQuery();

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();

                conditions.Add("tenant_id = @tenantId");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@tenantId", tenantId));

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@created_after", "users", "created_utc", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@created_before", "users", "created_utc", query.CreatedBefore.Value));
                }

                string whereClause = " WHERE " + string.Join(" AND ", conditions);
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                // Count
                long totalCount = 0;
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT COUNT(*) FROM users" + whereClause + ";";
                    foreach (NpgsqlParameter p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
                    totalCount = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                }

                // Query
                List<UserMaster> results = new List<UserMaster>();
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM users" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (NpgsqlParameter p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(UserColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }

                return EnumerationResult<UserMaster>.Create(query, results, totalCount);
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<UserMaster>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default)
        {
            if (query == null) query = new EnumerationQuery();

            using (NpgsqlConnection conn = new NpgsqlConnection(_Settings.GetConnectionString()))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@created_after", "users", "created_utc", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@created_before", "users", "created_utc", query.CreatedBefore.Value));
                }

                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                // Count
                long totalCount = 0;
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT COUNT(*) FROM users" + whereClause + ";";
                    foreach (NpgsqlParameter p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
                    totalCount = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                }

                // Query
                List<UserMaster> results = new List<UserMaster>();
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM users" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (NpgsqlParameter p in parameters) cmd.Parameters.Add(new NpgsqlParameter(p.ParameterName, p.Value));
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(UserColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }

                return EnumerationResult<UserMaster>.Create(query, results, totalCount);
            }
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
                    cmd.CommandText = "SELECT COUNT(*) FROM users WHERE tenant_id = @tenantId AND id = @id;";
                    StoredValueBinder.Value(cmd, "@tenantId", tenantId);
                    StoredValueBinder.Value(cmd, "@id", id);
                    long count = (long)(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                    return count > 0;
                }
            }
        }

        #endregion

    }
}

