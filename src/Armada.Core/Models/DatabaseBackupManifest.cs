namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Manifest stored as <c>manifest.json</c> inside a backup archive. Every value is read from the configured
    /// database provider when the backup is taken.
    /// </summary>
    public sealed class DatabaseBackupManifest
    {
        /// <summary>
        /// Backup time in UTC, ISO 8601.
        /// </summary>
        public string BackupTimestampUtc { get; set; } = String.Empty;

        /// <summary>
        /// Provider the backup was taken from.
        /// </summary>
        public DatabaseTypeEnum DatabaseType { get; set; }

        /// <summary>
        /// Applied schema version reported by the provider.
        /// </summary>
        public int SchemaVersion { get; set; }

        /// <summary>
        /// Armada version that wrote the archive.
        /// </summary>
        public string ArmadaVersion { get; set; } = String.Empty;

        /// <summary>
        /// Row counts per core table, read from the provider.
        /// </summary>
        public Dictionary<string, long> RecordCounts { get; set; } = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>
        /// Archive entry holding the native backup artifact.
        /// </summary>
        public string ArtifactEntry { get; set; } = String.Empty;

        /// <summary>
        /// Artifact location on the database host when the provider writes it there (SQL Server) and it is not
        /// readable by the admiral; empty when the artifact is inside the archive.
        /// </summary>
        public string ServerArtifactPath { get; set; } = String.Empty;

        /// <summary>
        /// Lower-case SHA-256 of the native backup artifact.
        /// </summary>
        public string ArtifactSha256 { get; set; } = String.Empty;

        /// <summary>
        /// Whether the provider-native backup was created and validated.
        /// </summary>
        public bool BackupValidated { get; set; }

        /// <summary>
        /// Whether the artifact was restored into an isolated target and verified.
        /// </summary>
        public bool RestoreVerified { get; set; }
    }
}
