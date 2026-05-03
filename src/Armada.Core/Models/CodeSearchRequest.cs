namespace Armada.Core.Models
{
    /// <summary>
    /// Request for searching a vessel code index.
    /// </summary>
    public class CodeSearchRequest
    {
        #region Public-Members

        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = "";

        /// <summary>
        /// Text query.
        /// </summary>
        public string Query { get; set; } = "";

        /// <summary>
        /// Maximum number of results.
        /// </summary>
        public int Limit { get; set; } = 10;

        /// <summary>
        /// Optional repository-relative path prefix filter.
        /// </summary>
        public string? PathPrefix { get; set; } = null;

        /// <summary>
        /// Optional language filter.
        /// </summary>
        public string? Language { get; set; } = null;

        /// <summary>
        /// Include full chunk content in each result.
        /// </summary>
        public bool IncludeContent { get; set; } = false;

        /// <summary>
        /// Include records marked reference-only.
        /// </summary>
        public bool IncludeReferenceOnly { get; set; } = false;

        #endregion
    }
}
