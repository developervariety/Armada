namespace Armada.Core.Models
{
    /// <summary>
    /// Request for searching code across all vessels in a fleet.
    /// </summary>
    public class FleetCodeSearchRequest
    {
        #region Public-Members

        /// <summary>
        /// Fleet identifier.
        /// </summary>
        public string FleetId { get; set; } = "";

        /// <summary>
        /// Text query.
        /// </summary>
        public string Query { get; set; } = "";

        /// <summary>
        /// Maximum number of results.
        /// 0 uses the per-vessel default multiplied by vessel count, capped at 50.
        /// </summary>
        public int Limit { get; set; } = 0;

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
