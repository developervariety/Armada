namespace Armada.Core.Models
{
    /// <summary>
    /// Request for graph-based affected test recommendations.
    /// </summary>
    public class CodeGraphAffectedTestsRequest
    {
        #region Public-Members

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Seed symbol name (qualified or simple).
        /// </summary>
        public string Symbol { get; set; } = "";

        /// <summary>
        /// Maximum traversal depth used to collect evidence.
        /// </summary>
        public int MaxDepth { get; set; } = 3;

        /// <summary>
        /// Maximum number of suggested test candidates.
        /// </summary>
        public int MaxResults { get; set; } = 20;

        #endregion
    }
}
