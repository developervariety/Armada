namespace Armada.Core.Models
{
    /// <summary>
    /// Canonical result for a tunnel-routed request.
    /// </summary>
    public class RemoteTunnelRequestResult
    {
        #region Public-Members

        /// <summary>
        /// HTTP-like status code for the request result.
        /// </summary>
        public int StatusCode { get; set; } = 200;

        /// <summary>
        /// Optional payload returned for successful requests.
        /// </summary>
        public object? Payload { get; set; } = null;

        /// <summary>
        /// Optional machine-readable error code.
        /// </summary>
        public string? ErrorCode { get; set; } = null;

        /// <summary>
        /// Optional human-readable message.
        /// </summary>
        public string? Message { get; set; } = null;

        /// <summary>
        /// Whether the request result is successful.
        /// </summary>
        public bool Success => StatusCode >= 200 && StatusCode < 300 && String.IsNullOrWhiteSpace(ErrorCode);

        #endregion
    }
}
