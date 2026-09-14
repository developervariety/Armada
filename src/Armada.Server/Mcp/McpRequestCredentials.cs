namespace Armada.Server.Mcp
{
    /// <summary>
    /// The credentials an MCP HTTP request presented. They are the same three headers the REST API
    /// accepts, so one authentication service decides both surfaces.
    /// </summary>
    public sealed class McpRequestCredentials
    {
        #region Public-Members

        /// <summary>
        /// Value of the Authorization header, for example a bearer credential.
        /// </summary>
        public string? Authorization { get; set; } = null;

        /// <summary>
        /// Value of the X-Token session header.
        /// </summary>
        public string? SessionToken { get; set; } = null;

        /// <summary>
        /// Value of the X-Api-Key header.
        /// </summary>
        public string? ApiKey { get; set; } = null;

        /// <summary>
        /// True when the request presented no credential at all.
        /// </summary>
        public bool IsEmpty =>
            System.String.IsNullOrWhiteSpace(Authorization)
            && System.String.IsNullOrWhiteSpace(SessionToken)
            && System.String.IsNullOrWhiteSpace(ApiKey);

        #endregion
    }
}
