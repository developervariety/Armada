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
    /// MySQL implementation of skill persistence.
    /// </summary>
    public class SkillMethods : ISkillMethods
    {
        private readonly string _ConnectionString;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public SkillMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <inheritdoc />
        public async Task<Skill> CreateAsync(Skill skill, CancellationToken token = default)
        {
            if (skill == null) throw new ArgumentNullException(nameof(skill));
            skill.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"INSERT INTO skills
                        (id, tenant_id, user_id, name, description, category, content, is_built_in, active, created_utc, last_update_utc)
                        VALUES
                        (@id, @tenant_id, @user_id, @name, @description, @category, @content, @is_built_in, @active, @created_utc, @last_update_utc);";
                    AddParameters(cmd, skill);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return skill;
        }

        /// <inheritdoc />
        public async Task<Skill?> ReadAsync(string id, SkillQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    List<string> conditions = new List<string> { "id = @id" };
                    List<MySqlParameter> parameters = new List<MySqlParameter> { StoredValueBinder.Parameter(new MySqlParameter(), "@id", id) };
                    ApplyQueryFilters(query, conditions, parameters);
                    cmd.CommandText = "SELECT * FROM skills WHERE " + String.Join(" AND ", conditions) + " LIMIT 1;";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return SkillColumns.Read(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }
            }

            return null;
        }

        /// <inheritdoc />
        public async Task<Skill> UpdateAsync(Skill skill, CancellationToken token = default)
        {
            if (skill == null) throw new ArgumentNullException(nameof(skill));
            skill.LastUpdateUtc = DateTime.UtcNow;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"UPDATE skills SET
                        tenant_id = @tenant_id,
                        user_id = @user_id,
                        name = @name,
                        description = @description,
                        category = @category,
                        content = @content,
                        is_built_in = @is_built_in,
                        active = @active,
                        last_update_utc = @last_update_utc
                        WHERE id = @id;";
                    AddParameters(cmd, skill);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return skill;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, SkillQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    List<string> conditions = new List<string> { "id = @id" };
                    List<MySqlParameter> parameters = new List<MySqlParameter> { StoredValueBinder.Parameter(new MySqlParameter(), "@id", id) };
                    ApplyQueryFilters(query, conditions, parameters);
                    cmd.CommandText = "DELETE FROM skills WHERE " + String.Join(" AND ", conditions) + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<Skill>> EnumerateAsync(SkillQuery query, CancellationToken token = default)
        {
            query ??= new SkillQuery();
            int pageSize = query.PageSize <= 0 ? 25 : query.PageSize;
            int offset = query.PageNumber <= 1 ? 0 : (query.PageNumber - 1) * pageSize;

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<MySqlParameter> parameters = new List<MySqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;

                long totalCount;
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM skills" + whereClause + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<Skill> results = new List<Skill>();
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM skills" + whereClause + " ORDER BY name ASC LIMIT " + pageSize + " OFFSET " + offset + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SkillColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }

                return new EnumerationResult<Skill>
                {
                    PageNumber = query.PageNumber,
                    PageSize = pageSize,
                    TotalRecords = totalCount,
                    TotalPages = pageSize > 0 ? (int)Math.Ceiling((double)totalCount / pageSize) : 0,
                    Objects = results
                };
            }
        }

        /// <inheritdoc />
        public async Task<List<Skill>> EnumerateAllAsync(SkillQuery query, CancellationToken token = default)
        {
            query ??= new SkillQuery();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    List<string> conditions = new List<string>();
                    List<MySqlParameter> parameters = new List<MySqlParameter>();
                    ApplyQueryFilters(query, conditions, parameters);
                    string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;
                    cmd.CommandText = "SELECT * FROM skills" + whereClause + " ORDER BY name ASC;";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(parameter);

                    List<Skill> results = new List<Skill>();
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(SkillColumns.Read(reader, MysqlDatabaseDriver.StoredValues));
                    }

                    return results;
                }
            }
        }

        private static void ApplyQueryFilters(SkillQuery? query, List<string> conditions, List<MySqlParameter> parameters)
        {
            if (query == null) return;

            if (!String.IsNullOrWhiteSpace(query.TenantId))
            {
                conditions.Add("tenant_id = @tenant_id");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@tenant_id", query.TenantId));
            }
            if (!String.IsNullOrWhiteSpace(query.UserId))
            {
                conditions.Add("user_id = @user_id");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@user_id", query.UserId));
            }
            if (!String.IsNullOrWhiteSpace(query.Category))
            {
                conditions.Add("category = @category");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@category", query.Category));
            }
            if (!String.IsNullOrWhiteSpace(query.Search))
            {
                conditions.Add("(LOWER(name) LIKE @search OR LOWER(COALESCE(description, '')) LIKE @search)");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@search", "%" + query.Search.ToLowerInvariant() + "%"));
            }
            if (query.Active.HasValue)
            {
                conditions.Add("active = @active");
                parameters.Add(MysqlDatabaseDriver.StoredBinder.Boolean(new MySqlParameter(), "@active", "skills", "active", query.Active.Value));
            }
            if (query.FromUtc.HasValue)
            {
                conditions.Add("created_utc >= @from_utc");
                parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@from_utc", "skills", "created_utc", query.FromUtc.Value));
            }
            if (query.ToUtc.HasValue)
            {
                conditions.Add("created_utc <= @to_utc");
                parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@to_utc", "skills", "created_utc", query.ToUtc.Value));
            }
        }

        private static void AddParameters(MySqlCommand cmd, Skill skill)
        {
            SkillColumns.Write(MysqlDatabaseDriver.StoredBinder.For(cmd, "skills"), skill);
        }

        private static MySqlParameter CloneParameter(MySqlParameter parameter)
        {
            return new MySqlParameter(parameter.ParameterName, parameter.Value);
        }
    }
}
