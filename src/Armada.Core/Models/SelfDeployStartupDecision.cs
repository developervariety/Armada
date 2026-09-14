namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Whether a normal admiral start may proceed given the durable restart record.
    /// </summary>
    public sealed class SelfDeployStartupDecision
    {
        /// <summary>
        /// Whether the server may start.
        /// </summary>
        public bool Allowed { get; set; }

        /// <summary>
        /// Stable reason.
        /// </summary>
        public string Reason { get; set; } = String.Empty;
    }
}
