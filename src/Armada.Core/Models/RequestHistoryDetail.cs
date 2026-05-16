namespace Armada.Core.Models
{
    /// <summary>
    /// Expanded request and response detail for one captured HTTP request.
    /// </summary>
    public class RequestHistoryDetail
    {
        #region Public-Members

        /// <summary>
        /// Parent request-history identifier.
        /// </summary>
        public string RequestHistoryId { get; set; } = string.Empty;

        /// <summary>
        /// Serialized path parameters JSON.
        /// </summary>
        public string? PathParamsJson { get; set; } = null;

        /// <summary>
        /// Serialized query parameters JSON.
        /// </summary>
        public string? QueryParamsJson { get; set; } = null;

        /// <summary>
        /// Serialized sanitized request headers JSON.
        /// </summary>
        public string? RequestHeadersJson { get; set; } = null;

        /// <summary>
        /// Serialized sanitized response headers JSON.
        /// </summary>
        public string? ResponseHeadersJson { get; set; } = null;

        /// <summary>
        /// Request body text snapshot.
        /// </summary>
        public string? RequestBodyText { get; set; } = null;

        /// <summary>
        /// Response body text snapshot.
        /// </summary>
        public string? ResponseBodyText { get; set; } = null;

        /// <summary>
        /// Whether the stored request body was truncated.
        /// </summary>
        public bool RequestBodyTruncated { get; set; } = false;

        /// <summary>
        /// Whether the stored response body was truncated.
        /// </summary>
        public bool ResponseBodyTruncated { get; set; } = false;

        #endregion
    }
}
