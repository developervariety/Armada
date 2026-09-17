namespace Armada.Server.WebSocket
{
    using System;

    /// <summary>
    /// A named refusal of a WebSocket command. A refused command reads and writes nothing.
    /// </summary>
    public sealed class WebSocketCommandRefusal
    {
        #region Public-Members

        /// <summary>
        /// Code for a command the handler does not declare.
        /// </summary>
        public const string UnknownCommandCode = "unknown_command";

        /// <summary>
        /// Code for a command sent without an authenticated caller.
        /// </summary>
        public const string AuthenticationRequiredCode = "authentication_required";

        /// <summary>
        /// Code for a command that needs a global administrator.
        /// </summary>
        public const string GlobalAdministratorRequiredCode = "global_administrator_required";

        /// <summary>
        /// Code for a record the caller may not read, reported exactly like a missing record so the reply never
        /// confirms that the record exists.
        /// </summary>
        public const string NotFoundCode = "not_found";

        /// <summary>
        /// Code for a record the caller may read but not change.
        /// </summary>
        public const string ForbiddenCode = "forbidden";

        /// <summary>
        /// Refusal code.
        /// </summary>
        public string Code { get; }

        /// <summary>
        /// Human-readable reason.
        /// </summary>
        public string Message { get; }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create a refusal.
        /// </summary>
        /// <param name="code">Refusal code.</param>
        /// <param name="message">Human-readable reason.</param>
        public WebSocketCommandRefusal(string code, string message)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Message = message ?? throw new ArgumentNullException(nameof(message));
        }

        /// <summary>
        /// Refusal for a command the handler does not declare.
        /// </summary>
        /// <param name="action">Command action.</param>
        /// <returns>The refusal.</returns>
        public static WebSocketCommandRefusal UnknownCommand(string action)
        {
            return new WebSocketCommandRefusal(UnknownCommandCode, "Unknown action: " + action);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// The command error frame for this refusal.
        /// </summary>
        /// <param name="action">Command action.</param>
        /// <returns>The frame to serialize.</returns>
        public object ToResult(string action)
        {
            return new { type = "command.error", action = action, error = Message, code = Code };
        }

        #endregion
    }
}
