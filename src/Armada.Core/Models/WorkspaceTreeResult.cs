namespace Armada.Core.Models
{
    /// <summary>
    /// Result of listing one workspace directory.
    /// </summary>
    public class WorkspaceTreeResult
    {
        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = string.Empty;

        /// <summary>
        /// Absolute workspace root path.
        /// </summary>
        public string RootPath { get; set; } = string.Empty;

        /// <summary>
        /// Current directory relative path using forward slashes.
        /// Empty means the workspace root.
        /// </summary>
        public string CurrentPath { get; set; } = string.Empty;

        /// <summary>
        /// Parent directory relative path, or null at root.
        /// </summary>
        public string? ParentPath { get; set; }

        /// <summary>
        /// Direct child entries of the current directory.
        /// </summary>
        public List<WorkspaceTreeEntry> Entries { get; set; } = new List<WorkspaceTreeEntry>();
    }
}
