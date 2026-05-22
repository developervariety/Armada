namespace Armada.Core.Models
{
    /// <summary>
    /// Request for symbol search against graph sidecars.
    /// </summary>
    public class CodeGraphSymbolSearchRequest
    {
        #region Public-Members

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Symbol query text.
        /// </summary>
        public string Query { get; set; } = "";

        /// <summary>
        /// Maximum number of results.
        /// </summary>
        public int Limit { get; set; } = 20;

        /// <summary>
        /// Optional repository-relative path prefix filter.
        /// </summary>
        public string? PathPrefix { get; set; } = null;

        /// <summary>
        /// Optional symbol kind filter.
        /// </summary>
        public CodeGraphSymbolKindEnum? Kind { get; set; } = null;

        #endregion
    }
}
