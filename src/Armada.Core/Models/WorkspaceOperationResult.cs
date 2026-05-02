namespace Armada.Core.Models
{
    /// <summary>
    /// Result of a non-read workspace operation.
    /// </summary>
    public class WorkspaceOperationResult
    {
        /// <summary>
        /// Affected repository-relative path.
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// New repository-relative path when the operation moved or renamed the entry.
        /// </summary>
        public string? NewPath { get; set; }

        /// <summary>
        /// Operation status string.
        /// </summary>
        public string Status { get; set; } = string.Empty;
    }
}
