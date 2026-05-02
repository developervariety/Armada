namespace Armada.Core.Models
{
    /// <summary>
    /// Request to rename or move a workspace entry.
    /// </summary>
    public class WorkspaceRenameRequest
    {
        /// <summary>
        /// Existing repository-relative path.
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// New repository-relative path.
        /// </summary>
        public string NewPath { get; set; } = string.Empty;
    }
}
