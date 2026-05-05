namespace Armada.Core.Enums
{
    /// <summary>
    /// Runbook execution lifecycle states.
    /// </summary>
    public enum RunbookExecutionStatusEnum
    {
        /// <summary>
        /// Execution is currently active.
        /// </summary>
        Running,

        /// <summary>
        /// Execution completed successfully.
        /// </summary>
        Completed,

        /// <summary>
        /// Execution was canceled before completion.
        /// </summary>
        Cancelled
    }
}
