namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Result of a completed restore.
    /// </summary>
    public sealed class DatabaseRestoreResult
    {
        /// <summary>
        /// Restore status; <c>restored</c> on success.
        /// </summary>
        public string Status { get; set; } = String.Empty;

        /// <summary>
        /// Safety backup taken before the database was replaced.
        /// </summary>
        public string SafetyBackupPath { get; set; } = String.Empty;

        /// <summary>
        /// Schema version recorded by the restored archive.
        /// </summary>
        public int SchemaVersion { get; set; }

        /// <summary>
        /// Operator message.
        /// </summary>
        public string Message { get; set; } = String.Empty;
    }
}
