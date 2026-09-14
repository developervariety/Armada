namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Result of validating a candidate server against an isolated restored database.
    /// </summary>
    public sealed class SelfDeployCandidateValidationResult
    {
        /// <summary>Whether candidate validation completed successfully.</summary>
        public bool CandidateValidated { get; set; }

        /// <summary>Stable failure code when candidate validation fails.</summary>
        public string FailureReason { get; set; } = String.Empty;

        /// <summary>Bounded diagnostic tail. Callers must provide sanitized text.</summary>
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
