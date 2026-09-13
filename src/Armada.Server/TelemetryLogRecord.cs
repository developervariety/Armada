namespace Armada.Server
{
    using System;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// One Admiral log entry prepared for export to a Loki or OTLP endpoint.
    /// </summary>
    public sealed class TelemetryLogRecord
    {
        #region Public-Members

        /// <summary>
        /// Exported log level.
        /// </summary>
        public LogLevel Level { get; set; } = LogLevel.Information;

        /// <summary>
        /// Redacted and bounded message, including any exception text.
        /// </summary>
        public string Message { get; set; } = String.Empty;

        #endregion
    }
}
