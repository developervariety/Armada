namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Who stopped a captain and from which surface. The reason on a stopped mission names both.
    /// </summary>
    public sealed class OperatorStopContext
    {
        #region Public-Members

        /// <summary>Surface name for a stop from the REST API.</summary>
        public const string RestSurface = "rest";

        /// <summary>Surface name for a stop from an MCP tool.</summary>
        public const string McpSurface = "mcp";

        /// <summary>Surface name for a stop from a WebSocket command.</summary>
        public const string WebSocketSurface = "websocket";

        /// <summary>Caller name; "operator" when the surface carries no identity.</summary>
        public string Who
        {
            get => _Who;
            set => _Who = String.IsNullOrWhiteSpace(value) ? "operator" : value.Trim();
        }

        /// <summary>Surface the stop came from.</summary>
        public string Surface
        {
            get => _Surface;
            set => _Surface = String.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        }

        #endregion

        #region Private-Members

        private string _Who = "operator";
        private string _Surface = "unknown";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public OperatorStopContext()
        {
        }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="who">Caller name, or null for "operator".</param>
        /// <param name="surface">Surface the stop came from.</param>
        public OperatorStopContext(string? who, string surface)
        {
            Who = who ?? "";
            Surface = surface;
        }

        #endregion
    }
}
