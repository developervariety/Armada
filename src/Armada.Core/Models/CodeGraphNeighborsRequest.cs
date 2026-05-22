namespace Armada.Core.Models
{
    /// <summary>
    /// Request for direct callers/callees graph queries.
    /// </summary>
    public class CodeGraphNeighborsRequest
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
        /// Maximum number of neighbor symbols to return.
        /// </summary>
        public int Limit { get; set; } = 25;

        #endregion
    }
}
