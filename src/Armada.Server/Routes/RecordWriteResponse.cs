namespace Armada.Server.Routes
{
    using System;
    using System.Text.Json;
    using WatsonWebserver.Core;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Maps a configuration-record write result to a REST response: the stored record with the success
    /// status, or an error body with 400 (invalid), 404 (not found), 403 (forbidden) or 409 (conflict).
    /// </summary>
    public static class RecordWriteResponse
    {
        #region Public-Methods

        /// <summary>
        /// Build the REST response for a write result.
        /// </summary>
        /// <typeparam name="T">Record type.</typeparam>
        /// <param name="req">API request whose response status is set.</param>
        /// <param name="result">Write result.</param>
        /// <param name="successStatus">Status for a successful write (200, 201 or 204).</param>
        /// <returns>The record, null for 204, or an error body.</returns>
        public static object? From<T>(ApiRequest req, RecordWriteResult<T> result, int successStatus) where T : class
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            if (result == null) throw new ArgumentNullException(nameof(result));

            switch (result.Outcome)
            {
                case RecordWriteOutcomeEnum.Succeeded:
                    req.Http.Response.StatusCode = successStatus;
                    return successStatus == 204 ? null : result.Record;
                case RecordWriteOutcomeEnum.Forbidden:
                    return RouteAuthRefusal.Forbid(req, result.Message ?? "");
                case RecordWriteOutcomeEnum.NotFound:
                    req.Http.Response.StatusCode = 404;
                    return new ApiErrorResponse { Error = ApiResultEnum.NotFound, Message = result.Message };
                case RecordWriteOutcomeEnum.Conflict:
                    req.Http.Response.StatusCode = 409;
                    return new ApiErrorResponse { Error = ApiResultEnum.Conflict, Message = result.Message };
                default:
                    req.Http.Response.StatusCode = 400;
                    return new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = result.Message };
            }
        }

        /// <summary>
        /// Read a request body into a write request. A body that is not valid for the request type (a malformed
        /// document, an unknown enum value, or a value a model setter refuses) is refused with 400 instead of
        /// failing the route.
        /// </summary>
        /// <typeparam name="T">Write request type.</typeparam>
        /// <param name="req">API request.</param>
        /// <param name="options">Serializer options.</param>
        /// <param name="body">The request, or null when refused.</param>
        /// <param name="refusal">The 400 error body when refused, otherwise null.</param>
        /// <returns>True when the body was read.</returns>
        public static bool TryReadBody<T>(ApiRequest req, JsonSerializerOptions options, out T? body, out object? refusal) where T : class, new()
        {
            if (req == null) throw new ArgumentNullException(nameof(req));
            body = null;
            refusal = null;
            string raw = req.Http.Request.DataAsString ?? "";
            try
            {
                body = String.IsNullOrWhiteSpace(raw) ? new T() : (JsonSerializer.Deserialize<T>(raw, options) ?? new T());
                return true;
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException)
            {
                // A model setter that refuses a value (such as a blank required name) throws while the body is
                // read; that is an invalid request, not a server fault.
                req.Http.Response.StatusCode = 400;
                refusal = new ApiErrorResponse { Error = ApiResultEnum.BadRequest, Message = "Request body is not valid: " + ex.Message };
                return false;
            }
        }

        #endregion
    }
}
