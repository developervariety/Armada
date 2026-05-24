namespace Armada.Core.Models
{
    /// <summary>
    /// Generic HTTP relay response returned from an Armada instance to Armada.Proxy.
    /// </summary>
    public class RemoteTunnelHttpRelayResponse
    {
        #region Public-Members

        /// <summary>
        /// HTTP status code.
        /// </summary>
        public int StatusCode { get; set; } = 200;

        /// <summary>
        /// Optional reason phrase.
        /// </summary>
        public string? ReasonPhrase { get; set; } = null;

        /// <summary>
        /// Selected response headers.
        /// </summary>
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Optional response content type.
        /// </summary>
        public string? ContentType { get; set; } = null;

        /// <summary>
        /// Optional base64-encoded response body.
        /// </summary>
        public string? BodyBase64 { get; set; } = null;

        #endregion
    }
}
