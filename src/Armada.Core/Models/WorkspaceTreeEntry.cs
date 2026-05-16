namespace Armada.Core.Models
{
    /// <summary>
    /// One file-system entry in a workspace tree listing.
    /// </summary>
    public class WorkspaceTreeEntry
    {
        /// <summary>
        /// Entry name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Repository-relative path using forward slashes.
        /// </summary>
        public string RelativePath { get; set; } = string.Empty;

        /// <summary>
        /// True if the entry is a directory.
        /// </summary>
        public bool IsDirectory { get; set; }

        /// <summary>
        /// True if the entry can be opened as editable text.
        /// </summary>
        public bool IsEditable { get; set; }

        /// <summary>
        /// File size when applicable.
        /// </summary>
        public long? SizeBytes { get; set; }

        /// <summary>
        /// Last write timestamp in UTC.
        /// </summary>
        public DateTime LastWriteUtc { get; set; }
    }
}
