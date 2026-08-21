namespace Armada.Server
{
    using System;
    using Radiant;
    using SyslogLogging;
    using Armada.Core;
    using Armada.Core.Settings;

    /// <summary>
    /// Owns the OpenTelemetry pipeline for the Admiral. When telemetry is enabled it starts a Radiant
    /// host that observes Armada's meter (<see cref="ArmadaMetrics.MeterName"/>) plus the web server and
    /// HTTP client instrumentation, exporting to an OTLP collector, an in-process Prometheus scrape
    /// endpoint, and/or Loki per <see cref="TelemetrySettings"/>. This is the one composition-root type
    /// that depends on Radiant; the rest of Armada emits through the base class library only.
    /// </summary>
    public class ArmadaTelemetryHost : IDisposable
    {
        #region Public-Members

        /// <summary>
        /// True when a telemetry host is running.
        /// </summary>
        public bool IsRunning => _Host != null;

        #endregion

        #region Private-Members

        private readonly LoggingModule _Logging;
        private readonly string _Header = "[ArmadaTelemetryHost] ";
        private RadiantHost? _Host = null;

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

                // Armada's own instruments, plus the web server and HTTP client instrumentation.
                // Subscribing to a name that emits nothing is harmless.
                radiant.Sources.AddMeter(ArmadaMetrics.MeterName);
                radiant.Sources.AddActivitySource(ArmadaMetrics.MeterName);
                radiant.Sources.AddMeter("Watson");
                radiant.Sources.AddMeter("Microsoft.AspNetCore.Hosting");
                radiant.Sources.AddMeter("System.Net.Http");

                _Host = RadiantHost.Start(radiant);

                _Logging.Info(_Header + "telemetry host started for service '" + settings.ServiceName + "'" +
                    (settings.PrometheusEnabled ? " (Prometheus scrape " + radiant.Prometheus.ToScrapeUrl() + ")" : "") +
                    (!String.IsNullOrWhiteSpace(settings.OtlpEndpoint) ? " (OTLP " + settings.OtlpEndpoint + ")" : ""));
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to start telemetry host: " + ex.Message);
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
                _Host?.Dispose();
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "error disposing telemetry host: " + ex.Message);
            }
            finally
            {
                _Host = null;
            }

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
