namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Starts, identifies, and stops supervised processes by verified identity (id plus start time).
    /// </summary>
    public interface ISelfDeployProcessHost
    {
        /// <summary>
        /// Capture the identity of a running process.
        /// </summary>
        /// <param name="processId">Process id.</param>
        /// <returns>Identity, or null when the process is not running or its start time cannot be read.</returns>
        SelfDeployProcessIdentity? Capture(int processId);

        /// <summary>
        /// Verified state of a recorded identity.
        /// </summary>
        /// <param name="identity">Recorded identity.</param>
        /// <returns>Running only when the id and start time both match.</returns>
        SelfDeployProcessStateEnum GetState(SelfDeployProcessIdentity identity);

        /// <summary>
        /// Start a process with argument-list execution.
        /// </summary>
        /// <param name="spec">Launch specification.</param>
        /// <returns>Identity of the started process.</returns>
        SelfDeployProcessIdentity Start(SelfDeployLaunchSpec spec);

        /// <summary>
        /// Terminate a process only when its identity still matches, then wait for confirmed exit.
        /// </summary>
        /// <param name="identity">Recorded identity.</param>
        /// <param name="entireProcessTree">Whether descendants are terminated too.</param>
        /// <param name="timeout">Maximum wait for confirmed exit.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True only when the identity is confirmed exited.</returns>
        Task<bool> TerminateAsync(
            SelfDeployProcessIdentity identity,
            bool entireProcessTree,
            TimeSpan timeout,
            CancellationToken token = default);

        /// <summary>
        /// Wait until a recorded identity is no longer running or the timeout elapses.
        /// </summary>
        /// <param name="identity">Recorded identity.</param>
        /// <param name="timeout">Maximum wait.</param>
        /// <param name="pollInterval">Delay between checks.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The final verified state.</returns>
        Task<SelfDeployProcessStateEnum> WaitForExitAsync(
            SelfDeployProcessIdentity identity,
            TimeSpan timeout,
            TimeSpan pollInterval,
            CancellationToken token = default);
    }
}
