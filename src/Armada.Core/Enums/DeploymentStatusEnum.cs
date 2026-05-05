namespace Armada.Core.Enums
{
    /// <summary>
    /// Lifecycle state for a deployment record.
    /// </summary>
    public enum DeploymentStatusEnum
    {
        /// <summary>
        /// Waiting for an explicit approval before execution.
        /// </summary>
        PendingApproval = 0,

        /// <summary>
        /// Running deploy commands or follow-up verification.
        /// </summary>
        Running = 1,

        /// <summary>
        /// Deploy command and any required verification completed successfully.
        /// </summary>
        Succeeded = 2,

        /// <summary>
        /// Deploy command succeeded but verification failed afterward.
        /// </summary>
        VerificationFailed = 3,

        /// <summary>
        /// Deployment failed before completion.
        /// </summary>
        Failed = 4,

        /// <summary>
        /// Approval was explicitly denied.
        /// </summary>
        Denied = 5,

        /// <summary>
        /// Rollback is currently executing.
        /// </summary>
        RollingBack = 6,

        /// <summary>
        /// Rollback completed successfully.
        /// </summary>
        RolledBack = 7
    }
}
