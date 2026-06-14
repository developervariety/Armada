namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;
    using Armada.Core.Settings;

    /// <summary>
    /// Runs the Release build gate for self-deploy.
    /// </summary>
    public interface ISelfDeployBuildRunner
    {
        /// <summary>
        /// Build the configured solution under the working directory.
        /// </summary>
        /// <param name="workingDirectory">Vessel WorkingDirectory path.</param>
        /// <param name="settings">Self-deploy settings.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Build result including exit code and output tail.</returns>
        Task<SelfDeployBuildResult> BuildAsync(
            string workingDirectory,
            SelfDeploySettings settings,
            CancellationToken token = default);
    }
}
