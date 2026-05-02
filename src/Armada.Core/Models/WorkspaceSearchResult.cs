namespace Armada.Core.Models
{
    /// <summary>
    /// Result of a workspace search.
    /// </summary>
    public class WorkspaceSearchResult
    {
        /// <summary>
        /// Search query.
        /// </summary>
        public string Query { get; set; } = string.Empty;

        /// <summary>
        /// Number of matches returned.
        /// </summary>
        public int TotalMatches { get; set; }

        /// <summary>
        /// True if the result set was truncated.
        /// </summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// Search matches.
        /// </summary>
        public List<WorkspaceSearchMatch> Matches { get; set; } = new List<WorkspaceSearchMatch>();
    }
}
