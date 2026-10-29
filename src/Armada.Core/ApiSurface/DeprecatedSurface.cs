namespace Armada.Core.ApiSurface
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The single list of covered public surfaces that are deprecated (docs/COMPATIBILITY.md, Deprecation): each keeps
    /// working unchanged until the next major release, which removes it. The Admiral marks every deprecated REST
    /// operation <c>deprecated: true</c> in OpenAPI with <see cref="Marker"/> on its summary, and prefixes the description
    /// of every deprecated MCP tool with <see cref="Marker"/>; the description names the replacement.
    /// </summary>
    public static class DeprecatedSurface
    {
        #region Public-Members

        /// <summary>
        /// Prefix added to OpenAPI summaries and MCP tool descriptions of deprecated items.
        /// </summary>
        public const string Marker = "[Deprecated] ";

        /// <summary>
        /// Major release that removes the deprecated items.
        /// </summary>
        public const string RemovedIn = "2.0";

        #endregion

        #region Private-Members

        // "METHOD template" to its replacement. Empty: the whole-list routes deprecated in 1.x were retired (RetiredSurface).
        private static readonly Dictionary<string, string> _Routes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
        };

        // MCP tool name to its replacement. Empty: see RetiredSurface.
        private static readonly Dictionary<string, string> _Tools = new Dictionary<string, string>(StringComparer.Ordinal)
        {
        };

        // WebSocket command action to its replacement.
        private static readonly Dictionary<string, string> _WebSocketCommands = new Dictionary<string, string>(StringComparer.Ordinal)
        {
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// The replacement for a deprecated REST route, or null when the route is not deprecated.
        /// </summary>
        /// <param name="method">HTTP method.</param>
        /// <param name="template">Route template, for example /api/v1/inbox.</param>
        /// <returns>The replacement, or null.</returns>
        public static string? RouteReplacement(string method, string template)
        {
            if (String.IsNullOrEmpty(method) || String.IsNullOrEmpty(template)) return null;
            return _Routes.TryGetValue(method.ToUpperInvariant() + " " + template, out string? replacement) ? replacement : null;
        }

        /// <summary>
        /// The replacement for a deprecated MCP tool, or null when the tool is not deprecated.
        /// </summary>
        /// <param name="toolName">Tool name.</param>
        /// <returns>The replacement, or null.</returns>
        public static string? ToolReplacement(string toolName)
        {
            if (String.IsNullOrEmpty(toolName)) return null;
            return _Tools.TryGetValue(toolName, out string? replacement) ? replacement : null;
        }

        /// <summary>
        /// The replacement for a deprecated WebSocket command, or null when the command is not deprecated.
        /// </summary>
        /// <param name="action">Command action.</param>
        /// <returns>The replacement, or null.</returns>
        public static string? WebSocketCommandReplacement(string action)
        {
            if (String.IsNullOrEmpty(action)) return null;
            return _WebSocketCommands.TryGetValue(action, out string? replacement) ? replacement : null;
        }

        /// <summary>
        /// Every deprecated REST route, as "METHOD template".
        /// </summary>
        /// <returns>The routes.</returns>
        public static IReadOnlyCollection<string> Routes()
        {
            return _Routes.Keys;
        }

        /// <summary>
        /// Every deprecated MCP tool.
        /// </summary>
        /// <returns>The tool names.</returns>
        public static IReadOnlyCollection<string> Tools()
        {
            return _Tools.Keys;
        }

        /// <summary>
        /// Prefix a summary or description with <see cref="Marker"/> and append the replacement, unless already marked.
        /// </summary>
        /// <param name="text">Summary or description.</param>
        /// <param name="replacement">Replacement to name.</param>
        /// <returns>The marked text.</returns>
        public static string Mark(string? text, string replacement)
        {
            string value = text ?? String.Empty;
            if (value.StartsWith(Marker, StringComparison.Ordinal)) return value;
            return Marker + value + (String.IsNullOrEmpty(replacement) ? "" : " Use " + replacement + " instead; removed in " + RemovedIn + ".");
        }

        #endregion
    }
}
