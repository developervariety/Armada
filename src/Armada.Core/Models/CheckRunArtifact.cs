namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One structured artifact discovered for a check run.
    /// </summary>
    public class CheckRunArtifact
    {
        /// <summary>
        /// Artifact path relative to the working directory.
        /// </summary>
        public string Path
        {
            get => _Path;
            set
            {
                if (String.IsNullOrWhiteSpace(value)) throw new ArgumentNullException(nameof(Path));
                _Path = value.Trim();
            }
        }

        /// <summary>
        /// Artifact size in bytes when available.
        /// </summary>
        public long? SizeBytes { get; set; } = null;

        /// <summary>
        /// Last write timestamp when available.
        /// </summary>
        public DateTime? LastWriteUtc { get; set; } = null;

        private string _Path = String.Empty;
    }
}
