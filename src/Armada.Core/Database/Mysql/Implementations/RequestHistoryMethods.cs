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
    /// MySQL implementation of request-history database operations.
    /// </summary>
    public class RequestHistoryMethods : IRequestHistoryMethods
    {
        private readonly string _ConnectionString;

        /// <summary>
        /// Instantiate.
        /// </summary>
        public RequestHistoryMethods(string connectionString)
        {
            _ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <inheritdoc />
        public async Task<RequestHistoryRecord> CreateAsync(RequestHistoryEntry entry, RequestHistoryDetail? detail, CancellationToken token = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (MySqlTransaction tx = await conn.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    using (MySqlCommand cmd = conn.CreateCommand())
                    {
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
                        using (MySqlCommand cmd = conn.CreateCommand())
                        {
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
            }

            return new RequestHistoryRecord { Entry = entry, Detail = detail };
        }

        /// <inheritdoc />
        public async Task<RequestHistoryRecord?> ReadAsync(string id, RequestHistoryQuery? query = null, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string> { "id = @id" };
                List<MySqlParameter> parameters = new List<MySqlParameter> { StoredValueBinder.Parameter(new MySqlParameter(), "@id", id) };
                ApplyQueryFilters(query, conditions, parameters);

                RequestHistoryEntry? entry = null;
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM request_history WHERE " + string.Join(" AND ", conditions) + " LIMIT 1;";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            entry = RequestHistoryColumns.ReadEntry(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }

                if (entry == null) return null;

                RequestHistoryDetail? detail = null;
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM request_history_detail WHERE request_history_id = @id LIMIT 1;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            detail = RequestHistoryColumns.ReadDetail(reader, MysqlDatabaseDriver.StoredValues);
                    }
                }

                return new RequestHistoryRecord { Entry = entry, Detail = detail };
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<RequestHistoryEntry>> EnumerateAsync(RequestHistoryQuery query, CancellationToken token = default)
        {
            query ??= new RequestHistoryQuery();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<MySqlParameter> parameters = new List<MySqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

                long totalCount;
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM request_history" + whereClause + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<RequestHistoryEntry> results = new List<RequestHistoryEntry>();
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    int pageSize = query.PageSize <= 0 ? 25 : query.PageSize;
                    cmd.CommandText = "SELECT * FROM request_history" + whereClause
                        + " ORDER BY created_utc DESC LIMIT " + pageSize + " OFFSET " + query.Offset + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(RequestHistoryColumns.ReadEntry(reader, MysqlDatabaseDriver.StoredValues));
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

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<MySqlParameter> parameters = new List<MySqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

                List<RequestHistoryEntry> results = new List<RequestHistoryEntry>();
                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM request_history" + whereClause + " ORDER BY created_utc DESC;";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    using (MySqlDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(RequestHistoryColumns.ReadEntry(reader, MysqlDatabaseDriver.StoredValues));
                    }
                }

                return results;
            }
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, RequestHistoryQuery? query = null, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string> { "id = @id" };
                List<MySqlParameter> parameters = new List<MySqlParameter> { StoredValueBinder.Parameter(new MySqlParameter(), "@id", id) };
                ApplyQueryFilters(query, conditions, parameters);

                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM request_history WHERE " + string.Join(" AND ", conditions) + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<int> DeleteByFilterAsync(RequestHistoryQuery query, CancellationToken token = default)
        {
            query ??= new RequestHistoryQuery();

            using (MySqlConnection conn = new MySqlConnection(_ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<MySqlParameter> parameters = new List<MySqlParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

                using (MySqlCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM request_history" + whereClause + ";";
                    foreach (MySqlParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        private static void BindEntry(MySqlCommand cmd, RequestHistoryEntry entry)
        {
            RequestHistoryColumns.WriteEntry(MysqlDatabaseDriver.StoredBinder.For(cmd, "request_history"), entry);
        }

        private static void BindDetail(MySqlCommand cmd, RequestHistoryDetail detail)
        {
            RequestHistoryColumns.WriteDetail(MysqlDatabaseDriver.StoredBinder.For(cmd, "request_history_details"), detail);
        }

        private static MySqlParameter CloneParameter(MySqlParameter parameter)
        {
            return new MySqlParameter(parameter.ParameterName, parameter.Value);
        }

        private static void ApplyQueryFilters(RequestHistoryQuery? query, List<string> conditions, List<MySqlParameter> parameters)
        {
            if (query == null) return;

            if (!string.IsNullOrWhiteSpace(query.TenantId))
            {
                conditions.Add("tenant_id = @tenant_id");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@tenant_id", query.TenantId));
            }
            if (!string.IsNullOrWhiteSpace(query.UserId))
            {
                conditions.Add("user_id = @user_id");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@user_id", query.UserId));
            }
            if (query.ExcludedUserIds != null && query.ExcludedUserIds.Count > 0)
            {
                List<string> excluded = new List<string>();
                for (int i = 0; i < query.ExcludedUserIds.Count; i++)
                {
                    excluded.Add("@excluded_user_" + i);
                    parameters.Add(new MySqlParameter("@excluded_user_" + i, query.ExcludedUserIds[i]));
                }
                conditions.Add("(user_id IS NULL OR user_id NOT IN (" + string.Join(", ", excluded) + "))");
            }
            if (!string.IsNullOrWhiteSpace(query.CredentialId))
            {
                conditions.Add("credential_id = @credential_id");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@credential_id", query.CredentialId));
            }
            if (!string.IsNullOrWhiteSpace(query.Principal))
            {
                conditions.Add("principal_display LIKE @principal");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@principal", "%" + query.Principal + "%"));
            }
            if (!string.IsNullOrWhiteSpace(query.Method))
            {
                conditions.Add("UPPER(method) = @method");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@method", query.Method.ToUpperInvariant()));
            }
            if (!string.IsNullOrWhiteSpace(query.Route))
            {
                conditions.Add("route LIKE @route");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@route", "%" + query.Route + "%"));
            }
            if (query.StatusCode.HasValue)
            {
                conditions.Add("status_code = @status_code");
                parameters.Add(StoredValueBinder.Parameter(new MySqlParameter(), "@status_code", query.StatusCode.Value));
            }
            if (query.IsSuccess.HasValue)
            {
                conditions.Add("is_success = @is_success");
                parameters.Add(MysqlDatabaseDriver.StoredBinder.Boolean(new MySqlParameter(), "@is_success", "request_history", "is_success", query.IsSuccess.Value));
            }
            if (query.FromUtc.HasValue)
            {
                conditions.Add("created_utc >= @from_utc");
                parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@from_utc", "request_history", "created_utc", query.FromUtc.Value));
            }
            if (query.ToUtc.HasValue)
            {
                conditions.Add("created_utc <= @to_utc");
                parameters.Add(MysqlDatabaseDriver.StoredBinder.Timestamp(new MySqlParameter(), "@to_utc", "request_history", "created_utc", query.ToUtc.Value));
            }
        }
    }
}
