namespace Armada.Server.WebSocket
{
    using System;

    /// <summary>
    /// The declared authorization of one WebSocket command, with the REST route and MCP tool it mirrors.
    /// </summary>
    public sealed class WebSocketCommandRule
    {
        #region Public-Members

        /// <summary>
        /// Command action name.
        /// </summary>
        public string Action { get; }

        /// <summary>
        /// What the command does.
        /// </summary>
        public WebSocketCommandOperationEnum Operation { get; }

        /// <summary>
        /// The authorization rule the handler enforces before the command runs.
        /// </summary>
        public WebSocketCommandRuleEnum Rule { get; }

        /// <summary>
        /// HTTP method of the matching REST route, or null when there is none.
        /// </summary>
        public string? RestMethod { get; }

        /// <summary>
        /// Path of the matching REST route, with its parameters in braces, or null when there is none.
        /// </summary>
        public string? RestPath { get; }

        /// <summary>
        /// Name of the matching MCP tool, or null when there is none.
        /// </summary>
        public string? McpTool { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Declare a command rule.
        /// </summary>
        /// <param name="action">Command action name.</param>
        /// <param name="operation">What the command does.</param>
        /// <param name="rule">Authorization rule.</param>
        /// <param name="restMethod">Matching REST method, or null.</param>
        /// <param name="restPath">Matching REST path, or null.</param>
        /// <param name="mcpTool">Matching MCP tool, or null.</param>
        public WebSocketCommandRule(
            string action,
            WebSocketCommandOperationEnum operation,
            WebSocketCommandRuleEnum rule,
            string? restMethod,
            string? restPath,
            string? mcpTool)
        {
            if (String.IsNullOrWhiteSpace(action)) throw new ArgumentNullException(nameof(action));
            if ((restMethod == null) != (restPath == null))
                throw new ArgumentException("A REST counterpart needs both a method and a path.", nameof(restPath));
            Action = action;
            Operation = operation;
            Rule = rule;
            RestMethod = restMethod;
            RestPath = restPath;
            McpTool = mcpTool;
        }

        #endregion
    }
}
