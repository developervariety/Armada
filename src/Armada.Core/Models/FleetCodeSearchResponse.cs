namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Response from a fleet-wide code index search.
    /// </summary>
    public class FleetCodeSearchResponse
    {
        #region Public-Members

        /// <summary>
        /// Fleet identifier.
        /// </summary>
        public string FleetId { get; set; } = "";

        /// <summary>
        /// Original query text.
        /// </summary>
        public string Query { get; set; } = "";

        /// <summary>
        /// Matching results merged across vessels.
        /// </summary>
        public List<FleetCodeSearchResult> Results { get; set; } = new List<FleetCodeSearchResult>();

        /// <summary>
        /// Non-blocking warnings emitted while searching fleet vessels.
        /// </summary>
        public List<string> Warnings { get; set; } = new List<string>();

        #endregion
    }

    /// <summary>
    /// A scored fleet search result with vessel attribution.
    /// </summary>
    public class FleetCodeSearchResult
    {
        #region Public-Members

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Vessel display name.
        /// </summary>
        public string VesselName { get; set; } = "";

        /// <summary>
        /// Search score. Higher is better.
        /// </summary>
        public double Score { get; set; } = 0;

        /// <summary>
        /// Indexed record metadata and optional content.
        /// </summary>
        public CodeIndexRecord Record { get; set; } = new CodeIndexRecord();

        /// <summary>
        /// Short matching excerpt.
        /// </summary>
        public string Excerpt { get; set; } = "";

        #endregion
    }
}
