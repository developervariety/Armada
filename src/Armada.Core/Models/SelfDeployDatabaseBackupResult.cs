namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Proof returned after a self-deploy database backup is restored and checked in isolation.
    /// </summary>
    public sealed class SelfDeployDatabaseBackupResult
    {
        /// <summary>Whether the backup artifact was created and validated.</summary>
        public bool BackupValidated { get; set; }

        /// <summary>Whether the artifact was restored and checked in an isolated target.</summary>
        public bool RestoreVerified { get; set; }

        /// <summary>Path to the backup artifact, when one was created.</summary>
        public string ArtifactPath { get; set; } = String.Empty;

        /// <summary>Unique isolated database name or file path used for verification.</summary>
        public string IsolatedTarget { get; set; } = String.Empty;

        /// <summary>Opaque provider ownership token required for cleanup and candidate validation.</summary>
        public string OwnershipToken { get; set; } = String.Empty;

        /// <summary>Whether cleanup of an isolated target completed after a failed attempt.</summary>
        public bool CleanupSucceeded { get; set; }

        /// <summary>Stable failure code. Provider output is never required for this value.</summary>
        public string FailureReason { get; set; } = String.Empty;

        /// <summary>Bounded diagnostic tail already sanitized. Raw provider output is forbidden.</summary>
        public string OutputTail
        {
            get => _OutputTail;
            set
            {
                string normalized = value?.Trim() ?? String.Empty;
                _OutputTail = normalized.Substring(0, Math.Min(normalized.Length, 4096));
            }
        }

        private string _OutputTail = String.Empty;
    }
}
