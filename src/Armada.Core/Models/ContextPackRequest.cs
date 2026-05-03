namespace Armada.Core.Models
{
    /// <summary>
    /// Request for building a dispatch-ready code context pack.
    /// </summary>
    public class ContextPackRequest
    {
        #region Public-Members

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Mission goal used as the search query.
        /// </summary>
        public string Goal { get; set; } = "";

        /// <summary>
        /// Approximate token budget for the markdown pack.
        /// </summary>
        public int TokenBudget { get; set; } = 3000;

        /// <summary>
        /// Optional maximum result count.
        /// </summary>
        public int? MaxResults { get; set; } = null;

        #endregion
    }
}
