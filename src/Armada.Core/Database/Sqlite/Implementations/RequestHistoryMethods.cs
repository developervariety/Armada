namespace Armada.Core.Database.Sqlite.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using SyslogLogging;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// SQLite implementation of request-history database operations.
    /// </summary>
    public class RequestHistoryMethods : IRequestHistoryMethods
    {
        #region Private-Members

#pragma warning disable CS0414
        private readonly string _Header = "[RequestHistoryMethods] ";
#pragma warning restore CS0414
        private readonly SqliteDatabaseDriver _Driver;
        private readonly DatabaseSettings _Settings;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public RequestHistoryMethods(SqliteDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
            _Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task<RequestHistoryRecord> CreateAsync(RequestHistoryEntry entry, RequestHistoryDetail? detail, CancellationToken token = default)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                using (SqliteTransaction tx = conn.BeginTransaction())
                {
                    using (SqliteCommand cmd = conn.CreateCommand())
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
                        using (SqliteCommand cmd = conn.CreateCommand())
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

                    tx.Commit();
                }
            }

            return new RequestHistoryRecord { Entry = entry, Detail = detail };
        }

        /// <inheritdoc />
        public async Task<RequestHistoryRecord?> ReadAsync(string id, RequestHistoryQuery? query = null, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string> { "id = @id" };
                List<SqliteParameter> parameters = new List<SqliteParameter> { StoredValueBinder.Parameter(new SqliteParameter(), "@id", id) };
                ApplyQueryFilters(query, conditions, parameters);

                RequestHistoryEntry? entry = null;
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM request_history WHERE " + string.Join(" AND ", conditions) + " LIMIT 1;";
                    foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            entry = RequestHistoryColumns.ReadEntry(reader, SqliteDatabaseDriver.StoredValues);
                    }
                }

                if (entry == null) return null;

                RequestHistoryDetail? detail = null;
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM request_history_detail WHERE request_history_id = @id LIMIT 1;";
                    StoredValueBinder.Value(cmd, "@id", id);
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(token).ConfigureAwait(false))
                            detail = RequestHistoryColumns.ReadDetail(reader, SqliteDatabaseDriver.StoredValues);
                    }
                }

                return new RequestHistoryRecord { Entry = entry, Detail = detail };
            }
        }

        /// <inheritdoc />
        public async Task<EnumerationResult<RequestHistoryEntry>> EnumerateAsync(RequestHistoryQuery query, CancellationToken token = default)
        {
            query ??= new RequestHistoryQuery();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<SqliteParameter> parameters = new List<SqliteParameter>();
                ApplyQueryFilters(query, conditions, parameters);

                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

                long totalCount;
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM request_history" + whereClause + ";";
                    foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    totalCount = Convert.ToInt64(await cmd.ExecuteScalarAsync(token).ConfigureAwait(false));
                }

                List<RequestHistoryEntry> results = new List<RequestHistoryEntry>();
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    int pageSize = query.PageSize <= 0 ? 25 : query.PageSize;
                    int offset = query.PageNumber <= 1 ? 0 : (query.PageNumber - 1) * pageSize;
                    cmd.CommandText = "SELECT * FROM request_history" + whereClause
                        + " ORDER BY created_utc DESC LIMIT " + pageSize + " OFFSET " + offset + ";";
                    foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(RequestHistoryColumns.ReadEntry(reader, SqliteDatabaseDriver.StoredValues));
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

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<SqliteParameter> parameters = new List<SqliteParameter>();
                ApplyQueryFilters(query, conditions, parameters);

                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;
                List<RequestHistoryEntry> results = new List<RequestHistoryEntry>();
                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM request_history" + whereClause + " ORDER BY created_utc DESC;";
                    foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(CloneParameter(parameter));
                    using (SqliteDataReader reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                            results.Add(RequestHistoryColumns.ReadEntry(reader, SqliteDatabaseDriver.StoredValues));
                    }
                }

                return results;
            }
        }

        /// <inheritdoc />
        public async Task DeleteAsync(string id, RequestHistoryQuery? query = null, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);
                List<string> conditions = new List<string> { "id = @id" };
                List<SqliteParameter> parameters = new List<SqliteParameter> { StoredValueBinder.Parameter(new SqliteParameter(), "@id", id) };
                ApplyQueryFilters(query, conditions, parameters);

                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM request_history WHERE " + string.Join(" AND ", conditions) + ";";
                    foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task<int> DeleteByFilterAsync(RequestHistoryQuery query, CancellationToken token = default)
        {
            query ??= new RequestHistoryQuery();

            using (SqliteConnection conn = new SqliteConnection(_Driver.ConnectionString))
            {
                await conn.OpenAsync(token).ConfigureAwait(false);

                List<string> conditions = new List<string>();
                List<SqliteParameter> parameters = new List<SqliteParameter>();
                ApplyQueryFilters(query, conditions, parameters);
                string whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;

                using (SqliteCommand cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM request_history" + whereClause + ";";
                    foreach (SqliteParameter parameter in parameters) cmd.Parameters.Add(parameter);
                    return await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
        }

        #endregion

        #region Private-Methods

        private static void BindEntry(SqliteCommand cmd, RequestHistoryEntry entry)
        {
            RequestHistoryColumns.WriteEntry(SqliteDatabaseDriver.StoredBinder.For(cmd, "request_history"), entry);
        }

        private static void BindDetail(SqliteCommand cmd, RequestHistoryDetail detail)
        {
            RequestHistoryColumns.WriteDetail(SqliteDatabaseDriver.StoredBinder.For(cmd, "request_history_details"), detail);
        }

        private static SqliteParameter CloneParameter(SqliteParameter parameter)
        {
            return new SqliteParameter(parameter.ParameterName, parameter.Value);
        }

        private static void ApplyQueryFilters(RequestHistoryQuery? query, List<string> conditions, List<SqliteParameter> parameters)
        {
            if (query == null) return;

            if (!string.IsNullOrWhiteSpace(query.TenantId))
            {
                conditions.Add("tenant_id = @tenant_id");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@tenant_id", query.TenantId));
            }
            if (!string.IsNullOrWhiteSpace(query.UserId))
            {
                conditions.Add("user_id = @user_id");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@user_id", query.UserId));
            }
            if (query.ExcludedUserIds != null && query.ExcludedUserIds.Count > 0)
            {
                List<string> excluded = new List<string>();
                for (int i = 0; i < query.ExcludedUserIds.Count; i++)
                {
                    excluded.Add("@excluded_user_" + i);
                    parameters.Add(new SqliteParameter("@excluded_user_" + i, query.ExcludedUserIds[i]));
                }
                conditions.Add("(user_id IS NULL OR user_id NOT IN (" + string.Join(", ", excluded) + "))");
            }
            if (!string.IsNullOrWhiteSpace(query.CredentialId))
            {
                conditions.Add("credential_id = @credential_id");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@credential_id", query.CredentialId));
            }
            if (!string.IsNullOrWhiteSpace(query.Principal))
            {
                conditions.Add("principal_display LIKE @principal");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@principal", "%" + query.Principal + "%"));
            }
            if (!string.IsNullOrWhiteSpace(query.Method))
            {
                conditions.Add("UPPER(method) = @method");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@method", query.Method.ToUpperInvariant()));
            }
            if (!string.IsNullOrWhiteSpace(query.Route))
            {
                conditions.Add("route LIKE @route");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@route", "%" + query.Route + "%"));
            }
            if (query.StatusCode.HasValue)
            {
                conditions.Add("status_code = @status_code");
                parameters.Add(StoredValueBinder.Parameter(new SqliteParameter(), "@status_code", query.StatusCode.Value));
            }
            if (query.IsSuccess.HasValue)
            {
                conditions.Add("is_success = @is_success");
                parameters.Add(SqliteDatabaseDriver.StoredBinder.Boolean(new SqliteParameter(), "@is_success", "request_history", "is_success", query.IsSuccess.Value));
            }
            if (query.FromUtc.HasValue)
            {
                conditions.Add("created_utc >= @from_utc");
                parameters.Add(SqliteDatabaseDriver.StoredBinder.Timestamp(new SqliteParameter(), "@from_utc", "request_history", "created_utc", query.FromUtc.Value));
            }
            if (query.ToUtc.HasValue)
            {
                conditions.Add("created_utc <= @to_utc");
                parameters.Add(SqliteDatabaseDriver.StoredBinder.Timestamp(new SqliteParameter(), "@to_utc", "request_history", "created_utc", query.ToUtc.Value));
            }
        }

        #endregion
    }
}
