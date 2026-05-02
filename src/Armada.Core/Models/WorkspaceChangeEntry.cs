namespace Armada.Core.Models
{
    /// <summary>
    /// One changed file in the working tree.
    /// </summary>
    public class WorkspaceChangeEntry
    {
        /// <summary>
        /// Repository-relative path after the change.
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// Git porcelain-style status code.
        /// </summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// Original repository-relative path when the file was renamed.
        /// </summary>
        public string? OriginalPath { get; set; }
    }
}
