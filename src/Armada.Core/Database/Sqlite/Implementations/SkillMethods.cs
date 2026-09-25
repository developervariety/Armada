namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// SQLite implementation of skill persistence.
    /// </summary>
    public class SkillMethods : ISkillMethods
    {
        private readonly SqliteDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public SkillMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        /// <inheritdoc />
        public async Task<Skill> CreateAsync(Skill skill, CancellationToken token = default)
        {
            if (skill == null) throw new ArgumentNullException(nameof(skill));
            skill.LastUpdateUtc = DateTime.UtcNow;

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO skills
                (id, tenant_id, user_id, name, description, category, content, is_built_in, active, created_utc, last_update_utc)
                VALUES
                (@id, @tenant_id, @user_id, @name, @description, @category, @content, @is_built_in, @active, @created_utc, @last_update_utc);";
            AddParameters(cmd, skill);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return skill;
        }

        /// <inheritdoc />
        public async Task<Skill?> ReadAsync(string id, SkillQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();

            List<string> conditions = new List<string> { "id = @id" };
            List<SqliteParameter> parameters = new List<SqliteParameter> { StoredValueBinder.Parameter(new SqliteParameter(), "@id", id) };
            ApplyQueryFilters(query, conditions, parameters);
            cmd.CommandText = "SELECT * FROM skills WHERE " + String.Join(" AND ", conditions) + " LIMIT 1;";
            foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(parameter);

            using SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
                return SkillColumns.Read(reader, SqliteDatabaseDriver.StoredValues);
            return null;
        }

        /// <inheritdoc />
        public async Task<Skill> UpdateAsync(Skill skill, CancellationToken token = default)
        {
            if (skill == null) throw new ArgumentNullException(nameof(skill));
            skill.LastUpdateUtc = DateTime.UtcNow;

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();
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
            return skill;
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, SkillQuery? query = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();
            List<string> conditions = new List<string> { "id = @id" };
            List<SqliteParameter> parameters = new List<SqliteParameter> { StoredValueBinder.Parameter(new SqliteParameter(), "@id", id) };
            ApplyQueryFilters(query, conditions, parameters);
            cmd.CommandText = "DELETE FROM skills WHERE " + String.Join(" AND ", conditions) + ";";
            foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(parameter);
            await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<Skill>> EnumerateAsync(SkillQuery query, CancellationToken token = default)
        {
            query ??= new SkillQuery();

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);

            List<string> conditions = new List<string>();
            List<SqliteParameter> parameters = new List<SqliteParameter>();
            ApplyQueryFilters(query, conditions, parameters);
            string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;

            long totalCount;
            using (SqliteCommand countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM skills" + whereClause + ";";
                foreach (SqliteParameter parameter in parameters) countCmd.Parameters.Add(new SqliteParameter(parameter.ParameterName, parameter.Value));
                totalCount = (long)(await countCmd.ExecuteScalarAsync(token).ConfigureAwait(false))!;
            }

            List<Skill> results = new List<Skill>();
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT * FROM skills" + whereClause +
                    " ORDER BY name ASC" +
                    " LIMIT " + query.PageSize + " OFFSET " + query.Offset + ";";
                foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(new SqliteParameter(parameter.ParameterName, parameter.Value));
                using SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    results.Add(SkillColumns.Read(reader, SqliteDatabaseDriver.StoredValues));
            }

            return new EnumerationResult<Skill>
            {
                PageNumber = query.PageNumber,
                PageSize = query.PageSize,
                TotalRecords = totalCount,
                TotalPages = query.PageSize > 0 ? (int)Math.Ceiling((double)totalCount / query.PageSize) : 0,
                Objects = results
            };
        }

        /// <inheritdoc />
        public async Task<List<Skill>> EnumerateAllAsync(SkillQuery query, CancellationToken token = default)
        {
            query ??= new SkillQuery();

            using SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString);
            await conn.OpenAsync(token).ConfigureAwait(false);
            using SqliteCommand cmd = conn.CreateCommand();

            List<string> conditions = new List<string>();
            List<SqliteParameter> parameters = new List<SqliteParameter>();
            ApplyQueryFilters(query, conditions, parameters);
            string whereClause = conditions.Count > 0 ? " WHERE " + String.Join(" AND ", conditions) : String.Empty;
            cmd.CommandText = "SELECT * FROM skills" + whereClause + " ORDER BY name ASC;";
            foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(parameter);

            List<Skill> results = new List<Skill>();
            using SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                results.Add(SkillColumns.Read(reader, SqliteDatabaseDriver.StoredValues));
            return results;
        }

        private static void ApplyQueryFilters(SkillQuery? query, List<string> conditions, List<SqliteParameter> parameters)
        {
            if (query == null) return;

            if (!String.IsNullOrWhiteSpace(query.TenantId))
            {
                conditions.Add("tenant_id = @tenant_id");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@tenant_id", query.TenantId));
            }
            if (!String.IsNullOrWhiteSpace(query.UserId))
            {
                conditions.Add("user_id = @user_id");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@user_id", query.UserId));
            }
            if (!String.IsNullOrWhiteSpace(query.Category))
            {
                conditions.Add("category = @category");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@category", query.Category));
            }
            if (!String.IsNullOrWhiteSpace(query.Search))
            {
                conditions.Add("(LOWER(name) LIKE @search OR LOWER(COALESCE(description, '')) LIKE @search)");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@search", "%" + query.Search.ToLowerInvariant() + "%"));
            }
            if (query.Active.HasValue)
            {
                conditions.Add("active = @active");
                parameters.Add(SqliteDatabaseDriver.StoredBinder.Boolean(new SqliteParameter(), "@active", "skills", "active", query.Active.Value));
            }
            if (query.FromUtc.HasValue)
            {
                conditions.Add("created_utc >= @from_utc");
                parameters.Add(SqliteDatabaseDriver.StoredBinder.Timestamp(new SqliteParameter(), "@from_utc", "skills", "created_utc", query.FromUtc.Value));
            }
            if (query.ToUtc.HasValue)
            {
                conditions.Add("created_utc <= @to_utc");
                parameters.Add(SqliteDatabaseDriver.StoredBinder.Timestamp(new SqliteParameter(), "@to_utc", "skills", "created_utc", query.ToUtc.Value));
            }
        }

        private static void AddParameters(SqliteCommand cmd, Skill skill)
        {
            SkillColumns.Write(SqliteDatabaseDriver.StoredBinder.For(cmd, "skills"), skill);
        }

    }
}
