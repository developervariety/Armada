namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Builds the launch specifications for supervised self-deploy processes.
    /// </summary>
    public interface ISelfDeployLaunchPlanner
    {
        /// <summary>
        /// Launch a server from an immutable artifact as part of a supervised restart.
        /// </summary>
        /// <param name="artifact">Candidate or rollback artifact.</param>
        /// <param name="operationId">Restart operation id admitted by the startup guard.</param>
        /// <returns>Launch specification.</returns>
        SelfDeployLaunchSpec ForServer(SelfDeployReleaseArtifact artifact, string operationId);

        /// <summary>
        /// Launch the supervisor from the immutable rollback artifact.
        /// </summary>
        /// <param name="rollback">Rollback artifact captured from the running admiral.</param>
        /// <param name="operationId">Restart operation id.</param>
        /// <returns>Launch specification.</returns>
        SelfDeployLaunchSpec ForSupervisor(SelfDeployReleaseArtifact rollback, string operationId);
    }
}
