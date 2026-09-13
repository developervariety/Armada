namespace Armada.Server.WebSocket
{
    /// <summary>
    /// Client message that authenticates a WebSocket session before any other route.
    /// </summary>
    public class WebSocketAuthenticateRequest
    {
        #region Public-Members

        /// <summary>
        /// Route name; always "authenticate".
        /// </summary>
        public string Route { get; set; } = "authenticate";

        /// <summary>
        /// Bearer credential token or dashboard session token.
        /// </summary>
        public string? Token { get; set; } = null;

        /// <summary>
        /// Admiral API key.
        /// </summary>
        public string? ApiKey { get; set; } = null;

        #endregion
    }
}
