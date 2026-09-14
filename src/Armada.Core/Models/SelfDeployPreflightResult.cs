namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Evidence returned by a self-deploy safety preflight.
    /// </summary>
    public sealed class SelfDeployPreflightResult
    {
        /// <summary>
        /// Whether the provider created and validated a recoverable backup.
        /// </summary>
        public bool BackupValidated { get; set; }

        /// <summary>
        /// Whether the provider restored the backup into an isolated target and verified it.
        /// </summary>
        public bool RestoreVerified { get; set; }

        /// <summary>
        /// Whether the provider validated the built candidate before cutover.
        /// </summary>
        public bool CandidateValidated { get; set; }

        /// <summary>
        /// Provider or validation detail for a failed preflight.
        /// </summary>
        public string FailureReason { get; set; } = String.Empty;

        /// <summary>
        /// Bounded diagnostic tail already sanitized by the provider. Raw command output is forbidden.
        /// </summary>
        public string OutputTail
        {
            get => _OutputTail;
            set
            {
                string normalized = value?.Trim() ?? String.Empty;
                _OutputTail = normalized.Substring(0, Math.Min(normalized.Length, 4096));
            }
        }

        /// <summary>
        /// Whether all required proofs are present and cutover is permitted.
        /// </summary>
        public bool IsSafeToCutover => BackupValidated
            && RestoreVerified
            && CandidateValidated
            && String.IsNullOrWhiteSpace(FailureReason);

        private string _OutputTail = String.Empty;
    }
}
