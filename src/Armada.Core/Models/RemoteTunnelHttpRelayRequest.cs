namespace Armada.Core.Models
{
    /// <summary>
    /// Generic HTTP relay request sent from Armada.Proxy to a connected Armada instance.
    /// </summary>
    public class RemoteTunnelHttpRelayRequest
    {
        #region Public-Members

        /// <summary>
        /// HTTP method.
        /// </summary>
        public string Method { get; set; } = "GET";

        /// <summary>
        /// Absolute request path on the Armada server.
        /// </summary>
        public string Path { get; set; } = "/api/v1/status/health";

        /// <summary>
        /// Optional raw query string without the leading question mark.
        /// </summary>
        public string? QueryString { get; set; } = null;

        /// <summary>
        /// Selected request headers.
        /// </summary>
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Optional request content type.
        /// </summary>
        public string? ContentType { get; set; } = null;

        /// <summary>
        /// Optional base64-encoded request body.
        /// </summary>
        public string? BodyBase64 { get; set; } = null;

        #endregion
    }
}
