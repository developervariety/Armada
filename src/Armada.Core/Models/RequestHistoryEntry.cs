using System;
using Armada.Core;

namespace Armada.Core.Models
{
    /// <summary>
    /// Summary metadata for one captured HTTP request.
    /// </summary>
    public class RequestHistoryEntry
    {
        #region Public-Members

        /// <summary>
        /// Unique identifier.
        /// </summary>
        public string Id { get; set; } = Constants.RequestHistoryIdPrefix + Constants.IdGenerator.Generate();

        /// <summary>
        /// Tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// User identifier.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Credential identifier when available.
        /// </summary>
        public string? CredentialId { get; set; } = null;

        /// <summary>
        /// Human-readable principal label.
        /// </summary>
        public string? PrincipalDisplay { get; set; } = null;

        /// <summary>
        /// Authentication method used for the request.
        /// </summary>
        public string? AuthMethod { get; set; } = null;

        /// <summary>
        /// HTTP method.
        /// </summary>
        public string Method { get; set; } = "GET";

        /// <summary>
        /// Matched or raw route path.
        /// </summary>
        public string Route { get; set; } = "/";

        /// <summary>
        /// Optional route template.
        /// </summary>
        public string? RouteTemplate { get; set; } = null;

        /// <summary>
        /// Optional raw query string.
        /// </summary>
        public string? QueryString { get; set; } = null;

        /// <summary>
        /// Response status code.
        /// </summary>
        public int StatusCode { get; set; } = 200;

        /// <summary>
        /// Total request duration in milliseconds.
        /// </summary>
        public double DurationMs { get; set; } = 0;

        /// <summary>
        /// Request size in bytes.
        /// </summary>
        public long RequestSizeBytes { get; set; } = 0;

        /// <summary>
        /// Response size in bytes.
        /// </summary>
        public long ResponseSizeBytes { get; set; } = 0;

        /// <summary>
        /// Request content type.
        /// </summary>
        public string? RequestContentType { get; set; } = null;

        /// <summary>
        /// Response content type.
        /// </summary>
        public string? ResponseContentType { get; set; } = null;

        /// <summary>
        /// Whether the response status indicates success.
        /// </summary>
        public bool IsSuccess { get; set; } = true;

        /// <summary>
        /// Remote client IP address if known.
        /// </summary>
        public string? ClientIp { get; set; } = null;

        /// <summary>
        /// Optional correlation identifier.
        /// </summary>
        public string? CorrelationId { get; set; } = null;

        /// <summary>
        /// Timestamp the request completed capture.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        #endregion
    }
}
