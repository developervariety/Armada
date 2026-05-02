namespace Armada.Core.Models
{
    /// <summary>
    /// Contents and metadata for one workspace file.
    /// </summary>
    public class WorkspaceFileResponse
    {
        /// <summary>
        /// Vessel identifier.
        /// </summary>
        public string VesselId { get; set; } = string.Empty;

        /// <summary>
        /// Repository-relative path using forward slashes.
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// File name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// File content for text files or previews.
        /// </summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// Content hash used for optimistic concurrency checks.
        /// </summary>
        public string ContentHash { get; set; } = string.Empty;

        /// <summary>
        /// True if the file can be edited as text.
        /// </summary>
        public bool IsEditable { get; set; }

        /// <summary>
        /// True if the file appears to be binary.
        /// </summary>
        public bool IsBinary { get; set; }

        /// <summary>
        /// True if the file is too large for normal editing.
        /// </summary>
        public bool IsLarge { get; set; }

        /// <summary>
        /// True if the returned content is only a truncated preview.
        /// </summary>
        public bool PreviewTruncated { get; set; }

        /// <summary>
        /// File size in bytes.
        /// </summary>
        public long SizeBytes { get; set; }

        /// <summary>
        /// Last write timestamp in UTC.
        /// </summary>
        public DateTime LastWriteUtc { get; set; }

        /// <summary>
        /// Language hint derived from the file extension.
        /// </summary>
        public string Language { get; set; } = "plaintext";
    }
}
