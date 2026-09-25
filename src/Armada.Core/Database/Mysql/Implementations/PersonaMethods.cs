namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using MySqlConnector;
    using Armada.Core.Database;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// MySQL implementation of persona database operations.
    /// </summary>
    public class PersonaMethods : IPersonaMethods
    {
        #region Private-Members

        private string _ConnectionString;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with a MySQL connection string.
        /// </summary>
        /// <param name="connectionString">MySQL connection string.</param>
        public PersonaMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Create a persona.
        /// </summary>
        /// <param name="persona">Persona to create.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Created persona.</returns>
        public async Task<Persona> CreateAsync(Persona persona, CancellationToken token = default)
        {
            if (persona == null) throw new ArgumentNullException(nameof(persona));
            persona.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO personas (id, tenant_id, user_id, ownership_scope, name, description, prompt_template_name, is_built_in, default_playbooks, active, default_captain_id, minimum_tier, created_utc, last_update_utc)
                        VALUES (@id, @tenant_id, @user_id, @ownership_scope, @name, @description, @prompt_template_name, @is_built_in, @default_playbooks, @active, @default_captain_id, @minimum_tier, @created_utc, @last_update_utc);";
                    PersonaColumns.Write(MysqlDatabaseDriver.StoredBinder.For(cmd, "personas"), persona);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return persona;
        }

        /// <summary>
        /// Read a persona by identifier.
        /// </summary>
        /// <param name="id">Persona identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Persona or null if not found.</returns>
        public async Task<Persona?> ReadAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM personas WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return PersonaColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Read a persona by name.
        /// </summary>
        /// <param name="name">Persona name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Persona or null if not found.</returns>
        public async Task<Persona?> ReadByNameAsync(string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM personas WHERE name = @name;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return PersonaColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Read a persona by tenant and name.
        /// </summary>
        /// <param name="tenantId">Tenant identifier.</param>
        /// <param name="name">Persona name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Persona or null if not found.</returns>
        public async Task<Persona?> ReadByNameAsync(string tenantId, string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentNullException(nameof(tenantId));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM personas WHERE tenant_id = @tenant_id AND name = @name;";
                    StoredValueBinder.Value(cmd, "@tenant_id", tenantId);
                    StoredValueBinder.Value(cmd, "@name", name);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return PersonaColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Update a persona.
        /// </summary>
        /// <param name="persona">Persona with updated values.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Updated persona.</returns>
        public async Task<Persona> UpdateAsync(Persona persona, CancellationToken token = default)
        {
            if (persona == null) throw new ArgumentNullException(nameof(persona));
            persona.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE personas SET
                        tenant_id = @tenant_id,
                        user_id = @user_id,
                        ownership_scope = @ownership_scope,
                        name = @name,
                        description = @description,
                        prompt_template_name = @prompt_template_name,
                        default_captain_id = @default_captain_id,
                        is_built_in = @is_built_in,
                            minimum_tier = @minimum_tier,
                        default_playbooks = @default_playbooks,
                        active = @active,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    PersonaColumns.Write(MysqlDatabaseDriver.StoredBinder.For(cmd, "personas"), persona);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return persona;
        }

        /// <summary>
        /// Delete a persona by identifier.
        /// </summary>
        /// <param name="id">Persona identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task DeleteAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM personas WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Enumerate all personas.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of all personas.</returns>
        public async Task<List<Persona>> EnumerateAsync(CancellationToken token = default)
        {
            List<Persona> results = new List<Persona>();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM personas ORDER BY name;";
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(PersonaColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Enumerate personas with pagination and filtering.
        /// </summary>
        /// <param name="query">Enumeration query parameters.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Paginated enumeration result.</returns>
        public async Task<EnumerationResult<Persona>> EnumerateAsync(EnumerationQuery query, CancellationToken token = default)
        {
            if (query == null) query = new EnumerationQuery();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<MySqlParameter> parameters = new List<MySqlParameter>();

                if (query.CreatedAfter.HasValue)
                {
                    conditions.Add("created_utc > @created_after");
                    parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@created_after", "personas", "created_utc", query.CreatedAfter.Value));
                }
                if (query.CreatedBefore.HasValue)
                {
                    conditions.Add("created_utc < @created_before");
                    parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@created_before", "personas", "created_utc", query.CreatedBefore.Value));
                }

                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";
                string orderDirection = query.Order == EnumerationOrderEnum.CreatedAscending ? "ASC" : "DESC";

                // Count
                long totalCount = 0;
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM personas" + whereClause + ";";
                    foreach (MySqlParameter p in parameters) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                // Query
                List<Persona> results = new List<Persona>();
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM personas" + whereClause +
                        " ORDER BY created_utc " + orderDirection +
                        " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                    foreach (MySqlParameter p in parameters) cmd.Parameters.Add(new MySqlParameter(p.ParameterName, p.Value));
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(PersonaColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }

                return EnumerationResult<Persona>.Create(query, results, totalCount);
            }
        }

        /// <summary>
        /// Check if a persona exists by identifier.
        /// </summary>
        /// <param name="id">Persona identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the persona exists.</returns>
        public async Task<bool> ExistsAsync(string id, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM personas WHERE id = @id;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    long count = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                    return count > 0;
                }
            }
        }

        /// <summary>
        /// Check if a persona exists by name.
        /// </summary>
        /// <param name="name">Persona name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the persona exists.</returns>
        public async Task<bool> ExistsByNameAsync(string name, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM personas WHERE name = @name;";
                    StoredValueBinder.Value(cmd, "@name", name);
                    long count = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                    return count > 0;
                }
            }
        }

        #endregion

        #region Private-Methods

        private static DateTime ToDatabaseTimestamp(DateTime dt)
        {
            return MysqlDatabaseDriver.ToDatabaseTimestamp(dt);
        }

        #endregion
    }
}
