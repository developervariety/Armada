namespace Armada.Core.Settings
{
    /// <summary>
    /// Telemetry export settings. When <see cref="Enabled"/> is true the Admiral hosts an OpenTelemetry
    /// pipeline (via the Radiant host) that observes Armada's meters and the web server's request
    /// instrumentation, exporting to an OTLP collector, an in-process Prometheus scrape endpoint, and/or
    /// Loki. Disabled by default so a fresh install ships no telemetry surface until an operator opts in.
    /// </summary>
    public class TelemetrySettings
    {
        #region Public-Members

        /// <summary>
        /// Whether telemetry export is enabled. When false, no telemetry host is started and metric
        /// emission remains a no-op in the base class library.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Logical service name reported to the telemetry backend. Never null or empty.
        /// </summary>
        public string ServiceName
        {
            get => _ServiceName;
            set => _ServiceName = String.IsNullOrWhiteSpace(value) ? "armada" : value;
        }

        /// <summary>
        /// Optional OTLP collector endpoint (e.g. http://localhost:4317). When null or empty, OTLP
        /// push export is not configured and telemetry is served only via the Prometheus scrape endpoint.
        /// </summary>
        public string? OtlpEndpoint { get; set; } = null;

        /// <summary>
        /// Whether to serve an in-process Prometheus scrape endpoint (/metrics). Useful before any
        /// collector exists and the default path for a local Grafana stack scraping the Admiral directly.
        /// </summary>
        public bool PrometheusEnabled { get; set; } = true;

        /// <summary>
        /// TCP port for the in-process Prometheus scrape endpoint. Must be in range [1, 65535].
        /// </summary>
        public int PrometheusPort
        {
            get => _PrometheusPort;
            set
            {
                if (value < 1) value = 1;
                if (value > 65535) value = 65535;
                _PrometheusPort = value;
            }
        }

        /// <summary>
        /// Optional Loki push endpoint (e.g. http://localhost:3100) for direct log export. When null or
        /// empty, direct Loki export is not configured.
        /// </summary>
        public string? LokiEndpoint { get; set; } = null;

        #endregion

        #region Private-Members

        private string _ServiceName = "armada";
        private int _PrometheusPort = 9464;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public TelemetrySettings()
        {
        }

        #endregion
    }
}
