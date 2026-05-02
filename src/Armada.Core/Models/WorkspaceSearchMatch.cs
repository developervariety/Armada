namespace Armada.Core.Models
{
    /// <summary>
    /// One workspace search match.
    /// </summary>
    public class WorkspaceSearchMatch
    {
        /// <summary>
        /// Repository-relative file path.
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// One-based line number for the match.
        /// </summary>
        public int LineNumber { get; set; }

        /// <summary>
        /// Short preview line.
        /// </summary>
        public string Preview { get; set; } = string.Empty;
    }
}
