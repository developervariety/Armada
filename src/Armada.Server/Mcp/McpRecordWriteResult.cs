namespace Armada.Server.Mcp
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Maps a configuration-record write result to an MCP tool result: the stored record, or an error
    /// envelope carrying the refusal message and a stable code.
    /// </summary>
    public static class McpRecordWriteResult
    {
        #region Public-Methods

        /// <summary>
        /// Build the tool result for a write result.
        /// </summary>
        /// <typeparam name="T">Record type.</typeparam>
        /// <param name="result">Write result.</param>
        /// <returns>The record, or an error envelope.</returns>
        public static object From<T>(RecordWriteResult<T> result) where T : class
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (result.Succeeded) return result.Record!;
            return new { Error = result.Message, Code = CodeOf(result) };
        }

        /// <summary>
        /// Stable refusal code for a write result: the result's own code when it has one, otherwise one
        /// derived from the outcome.
        /// </summary>
        /// <typeparam name="T">Record type.</typeparam>
        /// <param name="result">Write result.</param>
        /// <returns>Code.</returns>
        public static string CodeOf<T>(RecordWriteResult<T> result) where T : class
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            if (!String.IsNullOrEmpty(result.ErrorCode)) return result.ErrorCode!;
            switch (result.Outcome)
            {
                case RecordWriteOutcomeEnum.NotFound: return "not_found";
                case RecordWriteOutcomeEnum.Forbidden: return "forbidden";
                case RecordWriteOutcomeEnum.Conflict: return "conflict";
                case RecordWriteOutcomeEnum.Invalid: return "invalid";
                default: return "";
            }
        }

        #endregion
    }
}
