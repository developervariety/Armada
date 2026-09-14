namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Outcome of a bounded health validation.
    /// </summary>
    public sealed class SelfDeployHealthResult
    {
        /// <summary>
        /// Whether the launched process proved health before the deadline.
        /// </summary>
        public bool Healthy { get; set; }

        /// <summary>
        /// Stable failure reason when not healthy.
        /// </summary>
        public string FailureReason { get; set; } = String.Empty;
    }
}
