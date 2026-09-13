namespace Armada.Runtimes
{
    using System;

    /// <summary>
    /// One tool call read back from a canonical "[ARMADA:ACTIVITY] tool" record by
    /// <see cref="ActivityRecords.TryParseToolActivity"/>.
    /// </summary>
    public sealed class ToolActivityRecord
    {
        #region Public-Members

        /// <summary>
        /// Normalized tool name.
        /// </summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>
        /// Redacted primary argument (file path, command, pattern), or null.
        /// </summary>
        public string? Detail { get; set; } = null;

        /// <summary>
        /// Canonical status (ok, error, error exit N, incomplete, or another single word), or null when the
        /// runtime reported none.
        /// </summary>
        public string? Status { get; set; } = null;

        /// <summary>
        /// True when the status reports a finished call: ok, error, or incomplete.
        /// </summary>
        public bool IsFinished
        {
            get
            {
                if (Status == null) return false;
                return Status == StructuredRuntimeLogFormatter.OkStatus
                    || Status == StructuredRuntimeLogFormatter.IncompleteStatus
                    || Status.StartsWith(StructuredRuntimeLogFormatter.ErrorStatus, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// True for ok, false for error or incomplete, null when the call has not finished.
        /// </summary>
        public bool? Succeeded
        {
            get
            {
                if (!IsFinished) return null;
                return Status == StructuredRuntimeLogFormatter.OkStatus;
            }
        }

        #endregion
    }
}
