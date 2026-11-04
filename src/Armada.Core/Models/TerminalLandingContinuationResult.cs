namespace Armada.Core.Models
{
    using System;

    /// <summary>Outcome of an operator request for a terminal landing continuation.</summary>
    public sealed class TerminalLandingContinuationResult
    {
        /// <summary>True when the grant is stored and the Judge waits to rerun at the regenerated candidate.</summary>
        public bool Granted { get; set; }

        /// <summary>Named refusal code, with the mission id where one applies; null when granted.</summary>
        public string? Reason { get; set; }

        /// <summary>The Judge's review binding after the grant; null on refusal.</summary>
        public GeneratedArtifactReviewBinding? Binding { get; set; }

        /// <summary>Build a refusal.</summary>
        public static TerminalLandingContinuationResult Refused(string reason) =>
            new TerminalLandingContinuationResult { Granted = false, Reason = reason ?? throw new ArgumentNullException(nameof(reason)) };
    }
}
