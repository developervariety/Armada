namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Result of a verified provider-native backup.
    /// </summary>
    public sealed class DatabaseBackupResult
    {
        /// <summary>
        /// Archive path.
        /// </summary>
        public string Path { get; set; } = String.Empty;

        /// <summary>
        /// Backup time in UTC, ISO 8601.
        /// </summary>
        public string TimestampUtc { get; set; } = String.Empty;

        /// <summary>
        /// Provider the backup was taken from.
        /// </summary>
        public DatabaseTypeEnum DatabaseType { get; set; }

        /// <summary>
        /// Applied schema version reported by the provider.
        /// </summary>
        public int SchemaVersion { get; set; }

        /// <summary>
        /// Artifact location on the database host when it is not inside the archive.
        /// </summary>
        public string ServerArtifactPath { get; set; } = String.Empty;

        /// <summary>
        /// Archive size in bytes.
        /// </summary>
        public long SizeBytes { get; set; }

        /// <summary>
        /// Row counts per core table, read from the provider.
        /// </summary>
        public Dictionary<string, long> RecordCounts { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);
    }
}
