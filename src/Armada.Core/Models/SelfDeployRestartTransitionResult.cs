namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Result of a compare-and-swap restart record transition.
    /// </summary>
    public sealed class SelfDeployRestartTransitionResult
    {
        /// <summary>
        /// Whether the expected state matched and the new record was written.
        /// </summary>
        public bool Applied { get; set; }

        /// <summary>
        /// Record after the attempt: the written record, or the current record when not applied.
        /// </summary>
        public SelfDeployRestartRecord? Record { get; set; }

        /// <summary>
        /// Stable reason when not applied.
        /// </summary>
        public string FailureReason { get; set; } = String.Empty;
    }
}
