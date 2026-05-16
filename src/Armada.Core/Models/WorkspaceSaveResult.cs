namespace Armada.Core.Models
{
    /// <summary>
    /// Result of saving a workspace file.
    /// </summary>
    public class WorkspaceSaveResult
    {
        /// <summary>
        /// Repository-relative path using forward slashes.
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// Updated content hash.
        /// </summary>
        public string ContentHash { get; set; } = string.Empty;

        /// <summary>
        /// File size in bytes.
        /// </summary>
        public long SizeBytes { get; set; }

        /// <summary>
        /// Last write timestamp in UTC.
        /// </summary>
        public DateTime LastWriteUtc { get; set; }

        /// <summary>
        /// True if the file was newly created.
        /// </summary>
        public bool Created { get; set; }
    }
}
