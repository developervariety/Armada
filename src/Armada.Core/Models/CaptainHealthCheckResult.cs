namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// The result of a single health check performed against a captain's direct API endpoint. These are
    /// ephemeral telemetry records held in a bounded in-memory history per distinct captain and endpoint
    /// URL; they are not persisted across Admiral restarts.
    /// </summary>
    public class CaptainHealthCheckResult
    {
        #region Public-Members

        /// <summary>
        /// Identifier of the captain the endpoint belongs to.
        /// </summary>
        public string CaptainId { get; set; } = string.Empty;

        /// <summary>
        /// The endpoint URL that was checked.
        /// </summary>
        public string EndpointUrl { get; set; } = string.Empty;

        /// <summary>
        /// UTC timestamp when the check completed.
        /// </summary>
        public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// True when the endpoint responded successfully within the timeout.
        /// </summary>
        public bool Healthy { get; set; } = false;

        /// <summary>
        /// Round-trip latency in milliseconds. Zero when the check failed before a response was received.
        /// </summary>
        public double LatencyMs { get; set; } = 0;

        /// <summary>
        /// HTTP status code returned by the endpoint, when a response was received; otherwise null.
        /// </summary>
        public int? StatusCode { get; set; } = null;

        /// <summary>
        /// Failure reason when the check was not healthy; otherwise null.
        /// </summary>
        public string? Error { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate an empty result.
        /// </summary>
        public CaptainHealthCheckResult()
        {
        }

        #endregion
    }
}
