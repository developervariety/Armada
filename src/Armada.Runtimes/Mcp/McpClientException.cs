namespace Armada.Runtimes.Mcp
{
    using System;

    /// <summary>
    /// Raised when an MCP request fails at the transport or protocol level: a non-success HTTP status, a
    /// JSON-RPC error, or a response that carries no result. Tool-call sites report the message to the model as a
    /// failed tool result instead of ending the agent loop.
    /// </summary>
    public class McpClientException : Exception
    {
        #region Public-Members

        /// <summary>
        /// HTTP status of the refused request, or null when the failure was not an HTTP status.
        /// </summary>
        public int? StatusCode { get; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="message">Error message.</param>
        public McpClientException(string message) : base(message)
        {
        }

        /// <summary>
        /// Instantiate with the HTTP status of the refused request.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="statusCode">HTTP status code.</param>
        public McpClientException(string message, int statusCode) : base(message)
        {
            StatusCode = statusCode;
        }

        /// <summary>
        /// Instantiate with an inner exception.
        /// </summary>
        /// <param name="message">Error message.</param>
        /// <param name="innerException">Inner exception.</param>
        public McpClientException(string message, Exception innerException) : base(message, innerException)
        {
        }

        #endregion
    }
}
