namespace Armada.Core.Models
{
    /// <summary>
    /// Request for building a dispatch-ready fleet context pack.
    /// </summary>
    public class FleetContextPackRequest
    {
        #region Public-Members

        /// <summary>
        /// Fleet identifier.
        /// </summary>
        public string FleetId { get; set; } = "";

        /// <summary>
        /// Mission goal used as the search query.
        /// </summary>
        public string Goal { get; set; } = "";

        /// <summary>
        /// Approximate token budget for the markdown pack.
        /// </summary>
        public int TokenBudget { get; set; } = 8000;

        /// <summary>
        /// Optional maximum result count per vessel.
        /// </summary>
        public int? MaxResultsPerVessel { get; set; } = null;

        #endregion
    }
}
