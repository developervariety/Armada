namespace Armada.Server
{
    using System;
    using Microsoft.Extensions.Logging;
    using SyslogLogging;
    using Armada.Core.Services;

    /// <summary>
    /// Prepares Admiral log entries for export to an external log endpoint. Exported text crosses the
    /// deployment boundary, so every message and exception text passes through the shared secret redactor
    /// and is bounded, and the exception object itself is never exported.
    /// </summary>
    public static class TelemetryLogExport
    {
        #region Public-Members

        /// <summary>
        /// Maximum exported characters per entry, including the truncation marker.
        /// </summary>
        public const int MaxMessageLength = 16000;

        #endregion

        #region Private-Members

        private const string _TruncationMarker = "...(truncated)";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Build the export record for one log entry, or null when the entry is not exported. Debug entries are
        /// not exported, which also keeps the telemetry host's own Debug diagnostics from feeding back into the
        /// exporter.
        /// </summary>
        /// <param name="entry">Log entry.</param>
        /// <returns>The redacted, bounded record, or null.</returns>
        public static TelemetryLogRecord? BuildRecord(LogEntry? entry)
        {
            if (entry == null || entry.Severity == Severity.Debug) return null;

            string text = entry.Message ?? String.Empty;
            if (entry.Exception != null)
            {
                text = text + Environment.NewLine + entry.Exception.ToString();
            }

            string redacted = SecretRedactor.Redact(text);
            if (redacted.Length > MaxMessageLength)
            {
                redacted = redacted.Substring(0, MaxMessageLength - _TruncationMarker.Length) + _TruncationMarker;
            }

            return new TelemetryLogRecord
            {
                Level = ToLogLevel(entry.Severity),
                Message = redacted
            };
        }

        #endregion

        #region Private-Methods

        private static LogLevel ToLogLevel(Severity severity)
        {
            switch (severity)
            {
                case Severity.Debug: return LogLevel.Debug;
                case Severity.Info: return LogLevel.Information;
                case Severity.Warn: return LogLevel.Warning;
                case Severity.Error: return LogLevel.Error;
                case Severity.Alert:
                case Severity.Critical:
                case Severity.Emergency: return LogLevel.Critical;
                default: return LogLevel.Information;
            }
        }

        #endregion
    }
}
