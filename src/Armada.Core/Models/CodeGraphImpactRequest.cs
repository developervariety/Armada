namespace Armada.Core.Models
{
    /// <summary>
    /// Request for bounded graph impact traversal.
    /// </summary>
    public class CodeGraphImpactRequest
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
        /// Traversal direction.
        /// </summary>
        public CodeGraphTraversalDirectionEnum Direction { get; set; } = CodeGraphTraversalDirectionEnum.Both;

        /// <summary>
        /// Maximum traversal depth.
        /// </summary>
        public int MaxDepth { get; set; } = 3;

        /// <summary>
        /// Maximum number of impacted symbols to return.
        /// </summary>
        public int MaxResults { get; set; } = 50;

        #endregion
    }
}
