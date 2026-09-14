namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// One bounded health attempt against a launched server.
    /// </summary>
    public interface ISelfDeployHealthProbe
    {
        /// <summary>
        /// Check health once. The response must come from a server started no earlier than the launched process.
        /// </summary>
        /// <param name="healthUrl">Health endpoint.</param>
        /// <param name="process">Launched process identity.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Health outcome with a stable reason.</returns>
        Task<SelfDeployHealthResult> CheckAsync(
            string healthUrl,
            SelfDeployProcessIdentity process,
            CancellationToken token = default);
    }
}
