namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The request_history and request_history_detail row-to-model contracts, shared by every provider.
    /// </summary>
    internal static class RequestHistoryColumns
    {
        /// <summary>
        /// Read a request_history row.
        /// </summary>
        /// <param name="record">Reader positioned on a request_history row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The request-history entry.</returns>
        internal static RequestHistoryEntry ReadEntry(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "RequestHistoryEntry");
            RequestHistoryEntry entry = new RequestHistoryEntry();
            entry.Id = row.Text("id");
            entry.TenantId = row.NullableText("tenant_id");
            entry.UserId = row.NullableText("user_id");
            entry.CredentialId = row.NullableText("credential_id");
            entry.PrincipalDisplay = row.NullableText("principal_display");
            entry.AuthMethod = row.NullableText("auth_method");
            entry.Method = row.Text("method");
            entry.Route = row.Text("route");
            entry.RouteTemplate = row.NullableText("route_template");
            entry.QueryString = row.NullableText("query_string");
            entry.StatusCode = row.Int("status_code");
            entry.DurationMs = row.Double("duration_ms");
            entry.RequestSizeBytes = row.Long("request_size_bytes");
            entry.ResponseSizeBytes = row.Long("response_size_bytes");
            entry.RequestContentType = row.NullableText("request_content_type");
            entry.ResponseContentType = row.NullableText("response_content_type");
            entry.IsSuccess = row.Bool("is_success");
            entry.ClientIp = row.NullableText("client_ip");
            entry.CorrelationId = row.NullableText("correlation_id");
            entry.CreatedUtc = row.Utc("created_utc");
            return entry;
        }

        /// <summary>
        /// Bind every stored request_history column an entry holds, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the request_history table.</param>
        /// <param name="entry">Row to bind.</param>
        internal static void WriteEntry(StoredParameters parameters, RequestHistoryEntry entry)
        {
            parameters
                .Text("id", entry.Id)
                .Text("tenant_id", entry.TenantId)
                .Text("user_id", entry.UserId)
                .Text("credential_id", entry.CredentialId)
                .Text("principal_display", entry.PrincipalDisplay)
                .Text("auth_method", entry.AuthMethod)
                .Text("method", entry.Method)
                .Text("route", entry.Route)
                .Text("route_template", entry.RouteTemplate)
                .Text("query_string", entry.QueryString)
                .Int("status_code", entry.StatusCode)
                .Double("duration_ms", entry.DurationMs)
                .Long("request_size_bytes", entry.RequestSizeBytes)
                .Long("response_size_bytes", entry.ResponseSizeBytes)
                .Text("request_content_type", entry.RequestContentType)
                .Text("response_content_type", entry.ResponseContentType)
                .Bool("is_success", entry.IsSuccess)
                .Text("client_ip", entry.ClientIp)
                .Text("correlation_id", entry.CorrelationId)
                .Utc("created_utc", entry.CreatedUtc);
        }

        /// <summary>
        /// Read a request_history_detail row.
        /// </summary>
        /// <param name="record">Reader positioned on a request_history_detail row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The request-history detail.</returns>
        internal static RequestHistoryDetail ReadDetail(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "RequestHistoryDetail");
            RequestHistoryDetail detail = new RequestHistoryDetail();
            detail.RequestHistoryId = row.Text("request_history_id");
            detail.PathParamsJson = row.NullableText("path_params_json");
            detail.QueryParamsJson = row.NullableText("query_params_json");
            detail.RequestHeadersJson = row.NullableText("request_headers_json");
            detail.ResponseHeadersJson = row.NullableText("response_headers_json");
            detail.RequestBodyText = row.NullableText("request_body_text");
            detail.ResponseBodyText = row.NullableText("response_body_text");
            detail.RequestBodyTruncated = row.Bool("request_body_truncated");
            detail.ResponseBodyTruncated = row.Bool("response_body_truncated");
            return detail;
        }

        /// <summary>
        /// Bind every stored request_history_details column a detail holds, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the request_history_details table.</param>
        /// <param name="detail">Row to bind.</param>
        internal static void WriteDetail(StoredParameters parameters, RequestHistoryDetail detail)
        {
            parameters
                .Text("request_history_id", detail.RequestHistoryId)
                .Text("path_params_json", detail.PathParamsJson)
                .Text("query_params_json", detail.QueryParamsJson)
                .Text("request_headers_json", detail.RequestHeadersJson)
                .Text("response_headers_json", detail.ResponseHeadersJson)
                .Text("request_body_text", detail.RequestBodyText)
                .Text("response_body_text", detail.ResponseBodyText)
                .Bool("request_body_truncated", detail.RequestBodyTruncated)
                .Bool("response_body_truncated", detail.ResponseBodyTruncated);
        }
    }
}
