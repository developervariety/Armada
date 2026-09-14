namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Immutable, content-addressed copy of a server build used as a cutover candidate or rollback target.
    /// </summary>
    public sealed class SelfDeployReleaseArtifact
    {
        /// <summary>
        /// Lower-case SHA-256 digest over the sorted relative paths and file contents.
        /// </summary>
        public string Digest { get; set; } = String.Empty;

        /// <summary>
        /// Absolute directory that holds the read-only artifact files.
        /// </summary>
        public string Directory { get; set; } = String.Empty;

        /// <summary>
        /// Entry assembly file name inside <see cref="Directory"/>.
        /// </summary>
        public string EntryAssembly { get; set; } = String.Empty;

        /// <summary>
        /// Number of files covered by the digest.
        /// </summary>
        public int FileCount { get; set; }
    }
}
