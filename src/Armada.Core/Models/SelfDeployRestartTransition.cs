namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// One recorded state change of a supervised restart.
    /// </summary>
    public sealed class SelfDeployRestartTransition
    {
        /// <summary>
        /// State entered.
        /// </summary>
        public SelfDeployRestartStateEnum State { get; set; }

        /// <summary>
        /// Time the state was written.
        /// </summary>
        public DateTime Utc { get; set; }

        /// <summary>
        /// Stable reason for the transition.
        /// </summary>
        public string Reason { get; set; } = String.Empty;
    }
}
