namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Captures and verifies immutable, content-addressed server artifacts.
    /// </summary>
    public interface ISelfDeployArtifactStore
    {
        /// <summary>
        /// Copy a server directory into the store and return its content-addressed artifact.
        /// </summary>
        /// <param name="sourceDirectory">Directory holding the server build.</param>
        /// <param name="entryAssembly">Entry assembly file name inside the directory.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Verified artifact.</returns>
        /// <exception cref="Armada.Core.Services.SelfDeployCutoverException">Capture failed closed.</exception>
        Task<SelfDeployReleaseArtifact> CaptureAsync(
            string sourceDirectory,
            string entryAssembly,
            CancellationToken token = default);

        /// <summary>
        /// Recompute an artifact digest.
        /// </summary>
        /// <param name="artifact">Artifact to verify.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Null when the artifact is intact; otherwise a stable failure reason.</returns>
        Task<string?> VerifyAsync(SelfDeployReleaseArtifact artifact, CancellationToken token = default);
    }
}
