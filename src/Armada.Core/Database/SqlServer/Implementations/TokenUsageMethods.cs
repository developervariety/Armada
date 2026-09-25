namespace Armada.Core.Database.SqlServer.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.SqlClient;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// SQL Server implementation of token-usage database operations.
    /// </summary>
    public class TokenUsageMethods : ITokenUsageMethods
    {
        #region Private-Members

        private readonly SqlServerDatabaseDriver _Driver;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public TokenUsageMethods(SqlServerDatabaseDriver driver)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<TokenUsageRecord> CreateAsync(TokenUsageRecord record, CancellationToken token = default)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqlCommand cmd = conn.CreateCommand())
                {
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

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string> { "id = @id" };
                List<SqlParameter> parameters = new List<SqlParameter> { StoredValueBinder.Parameter(new SqlParameter(), "@id", id) };
                ApplyQueryFilters(query, conditions, parameters);

                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT TOP 1 * FROM token_usage WHERE " + string.Join(" AND ", conditions) + ";";
                    foreach (SqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            return TokenUsageColumns.Read(reader, SqlServerDatabaseDriver.StoredValues);
                    }
                }

                return null;
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<TokenUsageRecord>> EnumerateAsync(TokenUsageQuery query, CancellationToken token = default)
        {
            query ??= new TokenUsageQuery();

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<SqlParameter> parameters = new List<SqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

                long totalCount;
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM token_usage" + whereClause + ";";
                    foreach (SqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<TokenUsageRecord> results = new List<TokenUsageRecord>();
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    int pageSize = query.PageSize <= 0 ? 25 : query.PageSize;
                    cmd.CommandText = "SELECT * FROM token_usage" + whereClause
                        + " ORDER BY created_utc DESC OFFSET @offset ROWS FETCH NEXT @page_size ROWS ONLY;";
                    foreach (SqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    StoredValueBinder.Value(cmd, "@offset", query.Offset);
                    StoredValueBinder.Value(cmd, "@page_size", pageSize);
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(TokenUsageColumns.Read(reader, SqlServerDatabaseDriver.StoredValues));
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

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<SqlParameter> parameters = new List<SqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

                List<TokenUsageRecord> results = new List<TokenUsageRecord>();
                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM token_usage" + whereClause + " ORDER BY created_utc DESC;";
                    foreach (SqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    using (SqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(TokenUsageColumns.Read(reader, SqlServerDatabaseDriver.StoredValues));
                    }
                }

                return results;
            }
        }

        /// <inheritdoc />
        public async Task<int> DeleteByFilterAsync(TokenUsageQuery query, CancellationToken token = default)
        {
            query ??= new TokenUsageQuery();

            using (SqlConnection conn = new SqlConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<SqlParameter> parameters = new List<SqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

                using (SqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM token_usage" + whereClause + ";";
                    foreach (SqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        #endregion

        #region Private-Methods

        private static void BindRecord(SqlCommand cmd, TokenUsageRecord record)
        {
            TokenUsageColumns.Write(SqlServerDatabaseDriver.StoredBinder.For(cmd, "token_usage"), record);
        }

        private static SqlParameter CloneParameter(SqlParameter parameter)
        {
            return new SqlParameter(parameter.ParameterName, parameter.Value);
        }

        private static void ApplyQueryFilters(TokenUsageQuery? query, List<string> conditions, List<SqlParameter> parameters)
        {
            if (query == null) return;

            if (!string.IsNullOrWhiteSpace(query.TenantId))
            {
                conditions.Add("tenant_id = @tenant_id");
                parameters.Add(StoredValueBinder.Parameter(new SqlParameter(), "@tenant_id", query.TenantId));
            }
            if (!string.IsNullOrWhiteSpace(query.UserId))
            {
                conditions.Add("user_id = @user_id");
                parameters.Add(StoredValueBinder.Parameter(new SqlParameter(), "@user_id", query.UserId));
            }
            if (!string.IsNullOrWhiteSpace(query.Model))
            {
                conditions.Add("model = @model");
                parameters.Add(StoredValueBinder.Parameter(new SqlParameter(), "@model", query.Model));
            }
            if (!string.IsNullOrWhiteSpace(query.Runtime))
            {
                conditions.Add("runtime = @runtime");
                parameters.Add(StoredValueBinder.Parameter(new SqlParameter(), "@runtime", query.Runtime));
            }
            if (!string.IsNullOrWhiteSpace(query.Source))
            {
                conditions.Add("source = @source");
                parameters.Add(StoredValueBinder.Parameter(new SqlParameter(), "@source", query.Source));
            }
            if (!string.IsNullOrWhiteSpace(query.VesselId))
            {
                conditions.Add("vessel_id = @vessel_id");
                parameters.Add(StoredValueBinder.Parameter(new SqlParameter(), "@vessel_id", query.VesselId));
            }
            if (!string.IsNullOrWhiteSpace(query.CaptainId))
            {
                conditions.Add("captain_id = @captain_id");
                parameters.Add(StoredValueBinder.Parameter(new SqlParameter(), "@captain_id", query.CaptainId));
            }
            if (!string.IsNullOrWhiteSpace(query.SourceId))
            {
                conditions.Add("source_id = @source_id");
                parameters.Add(StoredValueBinder.Parameter(new SqlParameter(), "@source_id", query.SourceId));
            }
            if (query.FromUtc.HasValue)
            {
                conditions.Add("created_utc >= @from_utc");
                parameters.Add(SqlServerDatabaseDriver.StoredBinder.Timestamp(new SqlParameter(), "@from_utc", "token_usage", "created_utc", query.FromUtc.Value));
            }
            if (query.ToUtc.HasValue)
            {
                conditions.Add("created_utc <= @to_utc");
                parameters.Add(SqlServerDatabaseDriver.StoredBinder.Timestamp(new SqlParameter(), "@to_utc", "token_usage", "created_utc", query.ToUtc.Value));
            }
        }

        #endregion
    }
}
