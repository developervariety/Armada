namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// Outcome of a supervisor or recovery run.
    /// </summary>
    public sealed class SelfDeployCutoverResult
    {
        /// <summary>
        /// Record state after the run, when a record was available.
        /// </summary>
        public SelfDeployRestartStateEnum? State { get; set; }

        /// <summary>
        /// Stable reason.
        /// </summary>
        public string Reason { get; set; } = String.Empty;

        /// <summary>
        /// Whether a healthy owner is proven: the candidate committed or the rollback artifact is healthy.
        /// </summary>
        public bool HealthyOwnerProven => State == SelfDeployRestartStateEnum.Committed
            || State == SelfDeployRestartStateEnum.RolledBack;
    }
}
