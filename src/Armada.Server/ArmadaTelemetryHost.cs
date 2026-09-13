namespace Armada.Server
{
    using System;
    using System.Threading;
    using Microsoft.Extensions.Logging;
    using Radiant;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Settings;

    /// <summary>
    /// Owns the OpenTelemetry pipeline for the Admiral. When telemetry is enabled it starts a Radiant
    /// host that observes Armada's meter (<see cref="ArmadaMetrics.MeterName"/>) plus the web server and
    /// HTTP client instrumentation, exporting to an OTLP collector, an in-process Prometheus scrape
    /// endpoint, and/or Loki per <see cref="TelemetrySettings"/>. When a Loki or OTLP endpoint is set, the
    /// Admiral log stream is exported too, redacted and bounded by <see cref="TelemetryLogExport"/>. This is
    /// the one composition-root type that depends on Radiant; the rest of Armada emits through the base class
    /// library only.
    /// </summary>
    public class ArmadaTelemetryHost : IDisposable
    {
        #region Public-Members

        /// <summary>
        /// True when a telemetry host is running.
        /// </summary>
        public bool IsRunning => _Host != null;

        /// <summary>
        /// True while the Admiral log stream is exported to a Loki or OTLP endpoint.
        /// </summary>
        public bool IsExportingLogs => _MessageForwarder != null;

        /// <summary>
        /// Number of log entries handed to the log exporter since this host was created.
        /// </summary>
        public long ExportedLogCount => Interlocked.Read(ref _ExportedLogCount);

        /// <summary>
        /// Number of log entries that could not be handed to the log exporter since this host was created.
        /// </summary>
        public long LogForwardingFailures => Interlocked.Read(ref _LogForwardingFailures);

        #endregion

        #region Private-Members

        /// <summary>
        /// Minimum Radiant log severity exported: Information. Debug stays local.
        /// </summary>
        private const int _MinimumExportedSeverity = 2;

        private readonly LoggingModule _Logging;
        private readonly string _Header = "[ArmadaTelemetryHost] ";
        private RadiantHost? _Host = null;
        private Microsoft.Extensions.Logging.ILogger? _LogExporter = null;
        private Action<LogEntry>? _MessageForwarder = null;
        private long _ExportedLogCount = 0;
        private long _LogForwardingFailures = 0;

        [ThreadStatic]
        private static bool _Forwarding;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate. The host is not started until <see cref="Start(TelemetrySettings)"/> is called.
        /// </summary>
        /// <param name="logging">Logging module. Required.</param>
        public ArmadaTelemetryHost(LoggingModule logging)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Start the telemetry host from the supplied settings. A no-op when settings are null or
        /// <see cref="TelemetrySettings.Enabled"/> is false, or when a host is already running.
        /// Failures are logged and swallowed so telemetry never blocks Admiral startup.
        /// </summary>
        /// <param name="settings">Telemetry settings.</param>
        public void Start(TelemetrySettings settings)
        {
            if (settings == null || !settings.Enabled) return;
            if (_Host != null) return;

            try
            {
                RadiantSettings radiant = new RadiantSettings(settings.ServiceName);
                radiant.DiagnosticCallback = message => _Logging.Debug(_Header + message);

                if (!String.IsNullOrWhiteSpace(settings.OtlpEndpoint))
                {
                    radiant.Otlp.Endpoint = settings.OtlpEndpoint;
                }
                else
                {
                    // OTLP push defaults on; disable it when no collector is configured so we do not
                    // spam a nonexistent default endpoint.
                    radiant.Otlp.Enable = false;
                }

                radiant.Prometheus.Enable = settings.PrometheusEnabled;
                if (settings.PrometheusEnabled)
                {
                    radiant.Prometheus.Port = settings.PrometheusPort;
                }

                if (!String.IsNullOrWhiteSpace(settings.LokiEndpoint))
                {
                    radiant.Loki.Enable = true;
                    radiant.Loki.Endpoint = settings.LokiEndpoint;
                }

                // Logs are exported only to a configured Loki or OTLP endpoint, at Information and above.
                bool exportLogs = !String.IsNullOrWhiteSpace(settings.LokiEndpoint) || !String.IsNullOrWhiteSpace(settings.OtlpEndpoint);
                radiant.Logs.Enable = exportLogs;
                radiant.Logs.MinimumSeverity = _MinimumExportedSeverity;

                // Armada's own instruments, plus the web server and HTTP client instrumentation.
                // Subscribing to a name that emits nothing is harmless.
                radiant.Sources.AddMeter(ArmadaMetrics.MeterName);
                radiant.Sources.AddActivitySource(ArmadaMetrics.MeterName);
                radiant.Sources.AddMeter("Watson");
                radiant.Sources.AddMeter("Microsoft.AspNetCore.Hosting");
                radiant.Sources.AddMeter("System.Net.Http");

                _Host = RadiantHost.Start(radiant);

                if (exportLogs)
                {
                    _LogExporter = _Host.CreateLogger("Armada.Admiral");
                    _MessageForwarder = ForwardLogEntry;
                    _Logging.MessageLogged += _MessageForwarder;
                }

                _Logging.Info(_Header + "telemetry host started for service '" + settings.ServiceName + "'" +
                    (settings.PrometheusEnabled ? " (Prometheus scrape " + radiant.Prometheus.ToScrapeUrl() + ")" : "") +
                    (!String.IsNullOrWhiteSpace(settings.OtlpEndpoint) ? " (OTLP " + settings.OtlpEndpoint + ")" : "") +
                    (exportLogs ? " (log export on)" : ""));
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to start telemetry host: " + ex.ToString());
                StopForwarding();
                _Host = null;
            }
        }

        /// <summary>
        /// Dispose the telemetry host, flushing pending telemetry.
        /// </summary>
        public void Dispose()
        {
            try
            {
                StopForwarding();
                _Host?.Dispose();
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error disposing telemetry host: " + ex.ToString());
            }
            finally
            {
                _Host = null;
            }

            GC.SuppressFinalize(this);
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Hand one Admiral log entry to the log exporter. A failure is counted, never thrown back into the
        /// caller's logging path, and never logged through the module it came from, so forwarding cannot recurse.
        /// </summary>
        private void ForwardLogEntry(LogEntry entry)
        {
            if (_Forwarding) return;
            Microsoft.Extensions.Logging.ILogger? exporter = _LogExporter;
            if (exporter == null) return;

            _Forwarding = true;
            try
            {
                TelemetryLogRecord? record = TelemetryLogExport.BuildRecord(entry);
                if (record == null) return;
                exporter.Log(record.Level, "{ArmadaMessage}", record.Message);
                Interlocked.Increment(ref _ExportedLogCount);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _LogForwardingFailures);
            }
            finally
            {
                _Forwarding = false;
            }
        }

        private void StopForwarding()
        {
            Action<LogEntry>? forwarder = _MessageForwarder;
            if (forwarder != null)
            {
                _Logging.MessageLogged -= forwarder;
                _MessageForwarder = null;
            }
            _LogExporter = null;
        }

        #endregion
    }
}
