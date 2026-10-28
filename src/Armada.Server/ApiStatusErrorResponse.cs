namespace Armada.Server
{
    using System.Text.Json.Serialization;
    using WatsonWebserver.Core;

    /// <summary>
    /// Machine-readable category for an HTTP status the framework error set does not name.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ApiStatusErrorCodeEnum
    {
        /// <summary>HTTP 501. The operation is not supported on this server.</summary>
        NotImplemented,

        /// <summary>HTTP 503. A service the operation needs is not available.</summary>
        ServiceUnavailable,

        /// <summary>HTTP 504. The operation timed out waiting on a dependency.</summary>
        GatewayTimeout
    }

    /// <summary>
    /// Error body for HTTP 501, 503 and 504. <see cref="ApiErrorResponse.Error"/> keeps the framework value the
    /// route already returned. <see cref="Code"/> names the HTTP status.
    /// </summary>
    public sealed class ApiStatusErrorResponse : ApiErrorResponse
    {
        /// <summary>Category that matches the HTTP status.</summary>
        public ApiStatusErrorCodeEnum Code { get; set; }

        /// <summary>HTTP 501. <c>Error</c> stays <see cref="ApiResultEnum.BadRequest"/>.</summary>
        /// <param name="message">Human-readable detail.</param>
        /// <returns>The error body.</returns>
        public static ApiStatusErrorResponse NotImplemented(string? message)
        {
            return Create(ApiResultEnum.BadRequest, ApiStatusErrorCodeEnum.NotImplemented, message);
        }

        /// <summary>HTTP 503. <c>Error</c> stays <see cref="ApiResultEnum.BadRequest"/>.</summary>
        /// <param name="message">Human-readable detail.</param>
        /// <returns>The error body.</returns>
        public static ApiStatusErrorResponse ServiceUnavailable(string? message)
        {
            return Create(ApiResultEnum.BadRequest, ApiStatusErrorCodeEnum.ServiceUnavailable, message);
        }

        /// <summary>HTTP 503. <paramref name="error"/> stays the value the route already returned.</summary>
        /// <param name="error">Existing framework error value.</param>
        /// <param name="message">Human-readable detail.</param>
        /// <returns>The error body.</returns>
        public static ApiStatusErrorResponse ServiceUnavailable(ApiResultEnum error, string? message)
        {
            return Create(error, ApiStatusErrorCodeEnum.ServiceUnavailable, message);
        }

        /// <summary>HTTP 504. <c>Error</c> stays <see cref="ApiResultEnum.BadRequest"/>.</summary>
        /// <param name="message">Human-readable detail.</param>
        /// <returns>The error body.</returns>
        public static ApiStatusErrorResponse GatewayTimeout(string? message)
        {
            return Create(ApiResultEnum.BadRequest, ApiStatusErrorCodeEnum.GatewayTimeout, message);
        }

        private static ApiStatusErrorResponse Create(ApiResultEnum error, ApiStatusErrorCodeEnum code, string? message)
        {
            ApiStatusErrorResponse response = new ApiStatusErrorResponse();
            response.Error = error;
            response.Code = code;
            response.Message = message;
            return response;
        }
    }
}
