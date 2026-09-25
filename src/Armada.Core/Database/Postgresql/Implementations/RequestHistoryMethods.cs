namespace Armada.Core.Database.Postgresql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Npgsql;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// PostgreSQL implementation of request-history database operations.
    /// </summary>
    public class RequestHistoryMethods : IRequestHistoryMethods
    {
        private readonly NpgsqlDataSource _DataSource;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public RequestHistoryMethods(NpgsqlDataSource dataSource)
        {
            _DataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        }

        /// <inheritdoc />
        public async Task<RequestHistoryRecord> CreateAsync(RequestHistoryEntry entry, RequestHistoryDetail? detail, CancellationToken token = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            using (NpgsqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
            {
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO request_history (
                            id, tenant_id, user_id, credential_id, principal_display, auth_method, method, route, route_template,
                            query_string, status_code, duration_ms, request_size_bytes, response_size_bytes, request_content_type,
                            response_content_type, is_success, client_ip, correlation_id, created_utc
                        ) VALUES (
                            @id, @tenant_id, @user_id, @credential_id, @principal_display, @auth_method, @method, @route, @route_template,
                            @query_string, @status_code, @duration_ms, @request_size_bytes, @response_size_bytes, @request_content_type,
                            @response_content_type, @is_success, @client_ip, @correlation_id, @created_utc
                        );";
                    BindEntry(cmd, entry);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                if (detail != null)
                {
                    using (NpgsqlCommand cmd = new NpgsqlCommand())
                    {
                        cmd.Connection = conn;
                        cmd.Transaction = tx;
                        cmd.CommandText = @"INSERT INTO request_history_detail (
                                request_history_id, path_params_json, query_params_json, request_headers_json, response_headers_json,
                                request_body_text, response_body_text, request_body_truncated, response_body_truncated
                            ) VALUES (
                                @request_history_id, @path_params_json, @query_params_json, @request_headers_json, @response_headers_json,
                                @request_body_text, @response_body_text, @request_body_truncated, @response_body_truncated
                            );";
                        BindDetail(cmd, detail);
                        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }

                await tx.CommitAsync(token).ConfigureAwait(false);
            }

            return new RequestHistoryRecord { Entry = entry, Detail = detail };
        }

        /// <inheritdoc />
        public async Task<RequestHistoryRecord?> ReadAsync(string id, RequestHistoryQuery? query = null, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                List<string> conditions = new List<string> { "id = @id" };
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter> { StoredValueBinder.Parameter(new NpgsqlParameter(), "@id", id) };
                ApplyQueryFilters(query, conditions, parameters);

                RequestHistoryEntry? entry = null;
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM request_history WHERE " + string.Join(" AND ", conditions) + " LIMIT 1;";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            entry = RequestHistoryColumns.ReadEntry(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }

                if (entry == null) return null;

                RequestHistoryDetail? detail = null;
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM request_history_detail WHERE request_history_id = @id LIMIT 1;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            detail = RequestHistoryColumns.ReadDetail(reader, PostgresqlDatabaseDriver.StoredValues);
                    }
                }

                return new RequestHistoryRecord { Entry = entry, Detail = detail };
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<RequestHistoryEntry>> EnumerateAsync(RequestHistoryQuery query, CancellationToken token = default)
        {
            query ??= new RequestHistoryQuery();

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
                    cmd.CommandText = "SELECT COUNT(*) FROM request_history" + whereClause + ";";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.Value));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<RequestHistoryEntry> results = new List<RequestHistoryEntry>();
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    int pageSize = query.PageSize <= 0 ? 25 : query.PageSize;
                    int offset = query.PageNumber <= 1 ? 0 : (query.PageNumber - 1) * pageSize;
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM request_history" + whereClause
                        + " ORDER BY created_utc DESC LIMIT " + pageSize + " OFFSET " + offset + ";";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.Value));
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(RequestHistoryColumns.ReadEntry(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }

                return EnumerationResult<RequestHistoryEntry>.Create(
                    new EnumerationQuery { PageNumber = query.PageNumber, PageSize = query.PageSize <= 0 ? 25 : query.PageSize },
                    results,
                    totalCount);
            }
        }

        /// <inheritdoc />
        public async Task<List<RequestHistoryEntry>> EnumerateForSummaryAsync(RequestHistoryQuery query, CancellationToken token = default)
        {
            query ??= new RequestHistoryQuery();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                List<string> conditions = new List<string>();
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

                List<RequestHistoryEntry> results = new List<RequestHistoryEntry>();
                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "SELECT * FROM request_history" + whereClause + " ORDER BY created_utc DESC;";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.Value));
                    using (NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(RequestHistoryColumns.ReadEntry(reader, PostgresqlDatabaseDriver.StoredValues));
                    }
                }

                return results;
            }
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, RequestHistoryQuery? query = null, CancellationToken token = default)
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
                    cmd.CommandText = "DELETE FROM request_history WHERE " + string.Join(" AND ", conditions) + ";";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<int> DeleteByFilterAsync(RequestHistoryQuery query, CancellationToken token = default)
        {
            query ??= new RequestHistoryQuery();

            using (NpgsqlConnection conn = await _DataSource.OpenConnectionAsync(token).ConfigureAwait(false))
            {
                List<string> conditions = new List<string>();
                List<NpgsqlParameter> parameters = new List<NpgsqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

                using (NpgsqlCommand cmd = new NpgsqlCommand())
                {
                    cmd.Connection = conn;
                    cmd.CommandText = "DELETE FROM request_history" + whereClause + ";";
                    foreach (NpgsqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        private static void BindEntry(NpgsqlCommand cmd, RequestHistoryEntry entry)
        {
            RequestHistoryColumns.WriteEntry(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "request_history"), entry);
        }

        private static void BindDetail(NpgsqlCommand cmd, RequestHistoryDetail detail)
        {
            RequestHistoryColumns.WriteDetail(PostgresqlDatabaseDriver.StoredBinder.For(cmd, "request_history_details"), detail);
        }

        private static void ApplyQueryFilters(RequestHistoryQuery? query, List<string> conditions, List<NpgsqlParameter> parameters)
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
            if (query.ExcludedUserIds != null && query.ExcludedUserIds.Count > 0)
            {
                List<string> excluded = new List<string>();
                for (int i = 0; i < query.ExcludedUserIds.Count; i++)
                {
                    excluded.Add("@excluded_user_" + i);
                    parameters.Add(new NpgsqlParameter("@excluded_user_" + i, query.ExcludedUserIds[i]));
                }
                conditions.Add("(user_id IS NULL OR user_id NOT IN (" + string.Join(", ", excluded) + "))");
            }
            if (!string.IsNullOrWhiteSpace(query.CredentialId))
            {
                conditions.Add("credential_id = @credential_id");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@credential_id", query.CredentialId));
            }
            if (!string.IsNullOrWhiteSpace(query.Principal))
            {
                conditions.Add("principal_display ILIKE @principal");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@principal", "%" + query.Principal + "%"));
            }
            if (!string.IsNullOrWhiteSpace(query.Method))
            {
                conditions.Add("UPPER(method) = @method");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@method", query.Method.ToUpperInvariant()));
            }
            if (!string.IsNullOrWhiteSpace(query.Route))
            {
                conditions.Add("route ILIKE @route");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@route", "%" + query.Route + "%"));
            }
            if (query.StatusCode.HasValue)
            {
                conditions.Add("status_code = @status_code");
                parameters.Add(StoredValueBinder.Parameter(new NpgsqlParameter(), "@status_code", query.StatusCode.Value));
            }
            if (query.IsSuccess.HasValue)
            {
                conditions.Add("is_success = @is_success");
                parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Boolean(new NpgsqlParameter(), "@is_success", "request_history", "is_success", query.IsSuccess.Value));
            }
            if (query.FromUtc.HasValue)
            {
                conditions.Add("created_utc >= @from_utc");
                // The schema stores created_utc as ISO-8601 TEXT (matching the other *_utc
                // columns). Binding a raw DateTime makes PostgreSQL look for a text >= timestamp
                // operator that does not exist (42883) and the summary/rollback flows fail.
                parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@from_utc", "request_history", "created_utc", query.FromUtc.Value));
            }
            if (query.ToUtc.HasValue)
            {
                conditions.Add("created_utc <= @to_utc");
                parameters.Add(PostgresqlDatabaseDriver.StoredBinder.Timestamp(new NpgsqlParameter(), "@to_utc", "request_history", "created_utc", query.ToUtc.Value));
            }
        }

    }
}
