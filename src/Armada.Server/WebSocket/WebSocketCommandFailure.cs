namespace Armada.Server.WebSocket
{
    using System;
    using System.Text.Json;
    using Armada.Core.Services;

    /// <summary>
    /// A <c>command.error</c> frame for a command that threw. The frame has the same fields as a refusal:
    /// <c>type</c>, <c>error</c> and <c>code</c>.
    /// </summary>
    public static class WebSocketCommandFailure
    {
        /// <summary>The command body is not valid for the command.</summary>
        public const string InvalidArgumentCode = "invalid_argument";

        /// <summary>The command needs a capability this server does not have.</summary>
        public const string UnavailableCode = "unavailable";

        /// <summary>The command conflicts with current state.</summary>
        public const string ConflictCode = "conflict";

        /// <summary>The command failed for a reason that is not one of the named categories.</summary>
        public const string InternalErrorCode = "internal_error";

        /// <summary>
        /// Build the frame for an exception. A body that is not valid JSON, or that does not match the command
        /// model, is <see cref="InvalidArgumentCode"/>.
        /// </summary>
        /// <param name="ex">Exception from reading or running the command.</param>
        /// <returns>The frame to serialize.</returns>
        public static object FromException(Exception ex)
        {
            if (ex == null) throw new ArgumentNullException(nameof(ex));
            if (ex is CollectionLimitExceededException limit)
            {
                return new
                {
                    type = "command.error",
                    error = limit.Message,
                    code = CollectionLimitExceededException.ErrorCode,
                    field = limit.Field,
                    limit = limit.Limit,
                    actual = limit.Actual
                };
            }
            return new { type = "command.error", error = ex.Message ?? "", code = CodeFor(ex) };
        }

        /// <summary>Category for an exception type.</summary>
        /// <param name="ex">Exception.</param>
        /// <returns>Refusal-shaped code.</returns>
        public static string CodeFor(Exception ex)
        {
            if (ex == null) throw new ArgumentNullException(nameof(ex));
            if (ex is CollectionLimitExceededException) return CollectionLimitExceededException.ErrorCode;
            if (ex is KeyNotFoundException) return WebSocketCommandRefusal.NotFoundCode;
            if (ex is ArgumentException || ex is JsonException) return InvalidArgumentCode;
            if (ex is UnauthorizedAccessException) return WebSocketCommandRefusal.ForbiddenCode;
            if (ex is NotSupportedException) return UnavailableCode;
            if (ex is InvalidOperationException) return ConflictCode;
            return InternalErrorCode;
        }
    }
}
