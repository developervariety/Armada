namespace Armada.Core.Models
{
    /// <summary>
    /// Save request for a workspace file.
    /// </summary>
    public class WorkspaceSaveRequest
    {
        /// <summary>
        /// Repository-relative path using forward slashes.
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// New text content.
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Expected hash from the prior read, used for optimistic concurrency.
        /// </summary>
        public string? ExpectedHash { get; set; }
    }
}
