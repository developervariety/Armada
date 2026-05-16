namespace Armada.Core.Models
{
    /// <summary>
    /// Working tree change summary for a vessel workspace.
    /// </summary>
    public class WorkspaceChangesResult
    {
        /// <summary>
        /// Current branch name, if available.
        /// </summary>
        public string BranchName { get; set; } = string.Empty;

        /// <summary>
        /// True if the working tree has any local changes.
        /// </summary>
        public bool IsDirty { get; set; }

        /// <summary>
        /// Number of commits ahead of the remote tracking branch.
        /// </summary>
        public int CommitsAhead { get; set; }

        /// <summary>
        /// Number of commits behind the remote tracking branch.
        /// </summary>
        public int CommitsBehind { get; set; }

        /// <summary>
        /// Changed files.
        /// </summary>
        public List<WorkspaceChangeEntry> Changes { get; set; } = new List<WorkspaceChangeEntry>();

        /// <summary>
        /// Optional git inspection error.
        /// </summary>
        public string? Error { get; set; }
    }
}
