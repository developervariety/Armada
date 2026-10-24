namespace Test.Shared.Infrastructure
{
    /// <summary>
    /// One row of DEPRECATION.md's Active deprecations table.
    /// </summary>
    public sealed class DeprecationRow
    {
        /// <summary>
        /// Row ID (D-001).
        /// </summary>
        public string Id { get; set; } = "";

        /// <summary>
        /// REST, MCP, WebSocket, or Behavior.
        /// </summary>
        public string Surface { get; set; } = "";

        /// <summary>
        /// The deprecated item ("GET /api/v1/inbox", a tool name).
        /// </summary>
        public string Item { get; set; } = "";

        /// <summary>
        /// Its replacement, as DeprecatedSurface states it.
        /// </summary>
        public string Replacement { get; set; } = "";

        /// <summary>
        /// Release that deprecated it.
        /// </summary>
        public string DeprecatedIn { get; set; } = "";

        /// <summary>
        /// Release that removes it.
        /// </summary>
        public string RemovedIn { get; set; } = "";

        /// <summary>
        /// Migration status of first-party clients.
        /// </summary>
        public string Clients { get; set; } = "";
    }
}
