namespace Armada.Test.Unit.Suites.Services
{
    using System;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using Armada.Core.Settings;
    using Armada.Server;
    using Armada.Test.Common;
    using SyslogLogging;

    /// <summary>
    /// Tests for exporting the Admiral log stream to a configured Loki or OTLP endpoint.
    /// </summary>
    public class TelemetryLogExportTests : TestSuite
    {
        /// <inheritdoc />
        public override string Name => "Telemetry Log Export";

        private static LoggingModule CreateLogging()
        {
            LoggingModule logging = new LoggingModule();
            logging.Settings.EnableConsole = false;
            return logging;
        }

        /// <inheritdoc />
        protected override async Task RunTestsAsync()
        {
            await RunTest("A log entry is exported with its mapped level, and Debug is not exported", () =>
            {
                AssertNull(TelemetryLogExport.BuildRecord(new LogEntry(Severity.Debug, "debug chatter")), "Debug is not exported");

                TelemetryLogRecord? info = TelemetryLogExport.BuildRecord(new LogEntry(Severity.Info, "mission started"));
                AssertNotNull(info, "Info is exported");
                AssertTrue(info!.Level == LogLevel.Information, "Info maps to Information");
                AssertEqual("mission started", info.Message, "The message is kept");

                AssertTrue(TelemetryLogExport.BuildRecord(new LogEntry(Severity.Warn, "w"))!.Level == LogLevel.Warning, "Warn maps to Warning");
                AssertTrue(TelemetryLogExport.BuildRecord(new LogEntry(Severity.Error, "e"))!.Level == LogLevel.Error, "Error maps to Error");
                AssertTrue(TelemetryLogExport.BuildRecord(new LogEntry(Severity.Alert, "a"))!.Level == LogLevel.Critical, "Alert maps to Critical");
                AssertTrue(TelemetryLogExport.BuildRecord(new LogEntry(Severity.Emergency, "m"))!.Level == LogLevel.Critical, "Emergency maps to Critical");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("Exported log text is redacted, includes exception text, and is bounded", () =>
            {
                TelemetryLogRecord? redactedWarning = TelemetryLogExport.BuildRecord(new LogEntry(Severity.Warn, "provider call failed password=supersecret12"));
                AssertFalse(redactedWarning!.Message.Contains("supersecret12", StringComparison.Ordinal), "A secret in the message is redacted");

                InvalidOperationException failure = new InvalidOperationException("token=abcdef123456 rejected");
                TelemetryLogRecord? withException = TelemetryLogExport.BuildRecord(new LogEntry(Severity.Error, "dispatch failed", failure));
                AssertTrue(withException!.Message.Contains("InvalidOperationException", StringComparison.Ordinal), "The exception type is included");
                AssertFalse(withException.Message.Contains("abcdef123456", StringComparison.Ordinal), "A secret in the exception text is redacted");

                TelemetryLogRecord? longEntry = TelemetryLogExport.BuildRecord(new LogEntry(Severity.Info, new string('x', TelemetryLogExport.MaxMessageLength * 2)));
                AssertTrue(longEntry!.Message.Length <= TelemetryLogExport.MaxMessageLength, "Exported text stays within the bound");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            await RunTest("The telemetry host exports logs only when a Loki or OTLP endpoint is configured", () =>
            {
                LoggingModule logging = CreateLogging();

                using (ArmadaTelemetryHost metricsOnly = new ArmadaTelemetryHost(logging))
                {
                    metricsOnly.Start(new TelemetrySettings { Enabled = true, PrometheusEnabled = false });
                    AssertFalse(metricsOnly.IsExportingLogs, "Without a log endpoint no log stream is exported");
                }

                ArmadaTelemetryHost withLoki = new ArmadaTelemetryHost(logging);
                withLoki.Start(new TelemetrySettings { Enabled = true, PrometheusEnabled = false, LokiEndpoint = "http://127.0.0.1:9" });
                Console.WriteLine("TELEMETRY running=" + withLoki.IsRunning + " exportingLogs=" + withLoki.IsExportingLogs);
                AssertTrue(withLoki.IsRunning, "The host starts");
                AssertTrue(withLoki.IsExportingLogs, "A configured Loki endpoint exports the Admiral log stream");
                long beforeProbe = withLoki.ExportedLogCount;
                logging.Info("telemetry export probe");
                AssertEqual(beforeProbe + 1, withLoki.ExportedLogCount, "One Info entry is exported once");
                logging.Debug("telemetry debug probe");
                AssertEqual(beforeProbe + 1, withLoki.ExportedLogCount, "A Debug entry is not exported");
                withLoki.Dispose();
                AssertFalse(withLoki.IsExportingLogs, "Dispose stops exporting the log stream");

                long afterDispose = withLoki.ExportedLogCount;
                logging.Info("after dispose");
                AssertEqual(afterDispose, withLoki.ExportedLogCount, "No entry is exported after dispose");

                withLoki.Start(new TelemetrySettings { Enabled = true, PrometheusEnabled = false, LokiEndpoint = "http://127.0.0.1:9" });
                long afterRestart = withLoki.ExportedLogCount;
                logging.Info("after restart");
                AssertEqual(afterRestart + 1, withLoki.ExportedLogCount, "After dispose and restart one entry is exported once, not twice");
                AssertEqual(0L, withLoki.LogForwardingFailures, "No forwarding failures");
                withLoki.Dispose();

                using (ArmadaTelemetryHost disabled = new ArmadaTelemetryHost(logging))
                {
                    disabled.Start(new TelemetrySettings { Enabled = false, LokiEndpoint = "http://127.0.0.1:9" });
                    AssertFalse(disabled.IsExportingLogs, "Disabled telemetry exports nothing");
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }
}
