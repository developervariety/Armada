namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Response payload describing a captain's recent endpoint health-check history.
    /// </summary>
    public class CaptainHealthResponse
    {
        #region Public-Members

        /// <summary>
        /// Identifier of the captain.
        /// </summary>
        public string CaptainId { get; set; } = string.Empty;

        /// <summary>
        /// The captain's configured direct API endpoint URL, or null when none is configured.
        /// </summary>
        public string? EndpointUrl { get; set; } = null;

        /// <summary>
        /// Total number of health checks retained in the recent history.
        /// </summary>
        public int TotalChecks { get; set; } = 0;

        /// <summary>
        /// Number of retained checks that were healthy.
        /// </summary>
        public int HealthyChecks { get; set; } = 0;

        /// <summary>
        /// The recent health-check results, oldest first.
        /// </summary>
        public List<CaptainHealthCheckResult> Results { get; set; } = new List<CaptainHealthCheckResult>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CaptainHealthResponse()
        {
        }

        #endregion
    }
}
