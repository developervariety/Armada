namespace Armada.Core.Models
{
    /// <summary>
    /// Request to create a workspace directory.
    /// </summary>
    public class WorkspaceCreateDirectoryRequest
    {
        /// <summary>
        /// Repository-relative directory path using forward slashes.
        /// </summary>
        public string Path { get; set; } = string.Empty;
    }
}
