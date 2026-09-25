namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// PostgreSQL implementation of token-usage database operations.
    /// </summary>
    public class TokenUsageMethods : ITokenUsageMethods
    {
        #region Private-Members

        private readonly NpgsqlDataSource _DataSource;
        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public TokenUsageMethods(NpgsqlDataSource dataSource)
        {
            _DataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<TokenUsageRecord> CreateAsync(TokenUsageRecord record, CancellationToken token = default)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = @"INSERT INTO token_usage (
                            id, tenant_id, user_id, model, runtime, source, source_id, vessel_id, captain_id,
                            input_tokens, output_tokens, cached_tokens, total_tokens, estimated, created_utc,
                            uncached_input_tokens, cache_read_input_tokens, cache_write_input_tokens, usage_rule
                        ) VALUES (
                            @id, @tenant_id, @user_id, @model, @runtime, @source, @source_id, @vessel_id, @captain_id,
                            @input_tokens, @output_tokens, @cached_tokens, @total_tokens, @estimated, @created_utc,
                            @uncached_input_tokens, @cache_read_input_tokens, @cache_write_input_tokens, @usage_rule
                        );";
                    BindRecord(cmd, record);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            return record;
        }

        /// <inheritdoc />
        public async Task<TokenUsageRecord?> ReadAsync(string id, TokenUsageQuery? query = null, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                List<string> conditions = new List<string> { "id = @id" };
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter> { StoredValueBinder.Parameter(new NpgsqlParameter(), "@id", id) };
                ApplyQueryFilters(query, conditions, parameters);

                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM token_usage WHERE " + string.Join(" AND ", conditions) + " LIMIT 1;";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return TokenUsageColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }

                return null;
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<TokenUsageRecord>> EnumerateAsync(TokenUsageQuery query, CancellationToken token = default)
        {
            query ??= new TokenUsageQuery();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                List<string> conditions = new List<string>();
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

                long totalCount;
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT COUNT(*) FROM token_usage" + whereClause + ";";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.Value));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<TokenUsageRecord> results = new List<TokenUsageRecord>();
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    int pageSize = query.PageSize <= 0 ? 25 : query.PageSize;
                    int offset = query.PageNumber <= 1 ? 0 : (query.PageNumber - 1) * pageSize;
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM token_usage" + whereClause
                        + " ORDER BY created_utc DESC LIMIT " + pageSize + " OFFSET " + offset + ";";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.Value));
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(TokenUsageColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }

                return EnumerationResult<TokenUsageRecord>.Create(
                    new EnumerationQuery { PageNumber = query.PageNumber, PageSize = query.PageSize <= 0 ? 25 : query.PageSize },
                    results,
                    totalCount);
            }
        }

        /// <inheritdoc />
        public async Task<List<TokenUsageRecord>> EnumerateForSummaryAsync(TokenUsageQuery query, CancellationToken token = default)
        {
            query ??= new TokenUsageQuery();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                List<string> conditions = new List<string>();
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

                List<TokenUsageRecord> results = new List<TokenUsageRecord>();
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM token_usage" + whereClause + " ORDER BY created_utc DESC;";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.Value));
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(TokenUsageColumns.Read(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }

                return results;
            }
        }

        /// <inheritdoc />
        public async Task<int> DeleteByFilterAsync(TokenUsageQuery query, CancellationToken token = default)
        {
            query ??= new TokenUsageQuery();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                List<string> conditions = new List<string>();
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM token_usage" + whereClause + ";";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        #endregion

        #region Private-Methods

        private static void BindRecord(NpgsqlCommand cmd, TokenUsageRecord record)
        {
            TokenUsageColumns.Write(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "token_usage"), record);
        }

        private static void ApplyQueryFilters(TokenUsageQuery? query, List<string> conditions, List<NpgsqlParameter> parameters)
        {
            if (query == null) return;

            if (!string.IsNullOrWhiteSpace(query.TenantId))
            {
                conditions.Add("tenant_id = @tenant_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@tenant_id", query.TenantId));
            }
            if (!string.IsNullOrWhiteSpace(query.UserId))
            {
                conditions.Add("user_id = @user_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@user_id", query.UserId));
            }
            if (!string.IsNullOrWhiteSpace(query.Model))
            {
                conditions.Add("model = @model");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@model", query.Model));
            }
            if (!string.IsNullOrWhiteSpace(query.Runtime))
            {
                conditions.Add("runtime = @runtime");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@runtime", query.Runtime));
            }
            if (!string.IsNullOrWhiteSpace(query.Source))
            {
                conditions.Add("source = @source");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@source", query.Source));
            }
            if (!string.IsNullOrWhiteSpace(query.VesselId))
            {
                conditions.Add("vessel_id = @vessel_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@vessel_id", query.VesselId));
            }
            if (!string.IsNullOrWhiteSpace(query.CaptainId))
            {
                conditions.Add("captain_id = @captain_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@captain_id", query.CaptainId));
            }
            if (!string.IsNullOrWhiteSpace(query.SourceId))
            {
                conditions.Add("source_id = @source_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@source_id", query.SourceId));
            }
            if (query.FromUtc.HasValue)
            {
                conditions.Add("created_utc >= @from_utc");
                parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@from_utc", "token_usage", "created_utc", query.FromUtc.Value));
            }
            if (query.ToUtc.HasValue)
            {
                conditions.Add("created_utc <= @to_utc");
                parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@to_utc", "token_usage", "created_utc", query.ToUtc.Value));
            }
        }

        #endregion
    }
}
