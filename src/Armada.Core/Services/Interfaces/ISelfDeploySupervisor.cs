namespace Armada.Core.Services.Interfaces
{
    /// <summary>
    /// Spawns an external supervisor that survives admiral process exit.
    /// </summary>
    public interface ISelfDeploySupervisor
    {
        /// <summary>
        /// Start the supervisor process that waits for the current admiral pid and
        /// launches the newly built server binary.
        /// </summary>
        /// <param name="workingDirectory">Vessel WorkingDirectory path.</param>
        /// <param name="admiralProcessId">Current admiral process id.</param>
        /// <param name="serverDllPath">Absolute path to Armada.Server.dll.</param>
        /// <param name="supervisorScriptPath">Absolute path to the watchdog script.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the supervisor process was spawned.</returns>
        Task<bool> RequestSupervisedRestartAsync(
            string workingDirectory,
            int admiralProcessId,
            string serverDllPath,
            string supervisorScriptPath,
            CancellationToken token = default);
    }
}
