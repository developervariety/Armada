namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Reports whether a container runtime is usable in a dock, so the definition-of-done gate can
    /// scope container-dependent tests out instead of running them into a guaranteed failure.
    /// </summary>
    public interface IContainerRuntimeProbe
    {
        /// <summary>
        /// Determine whether a container runtime is available and responding.
        /// </summary>
        /// <param name="workingDirectory">Directory to probe from.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when a container runtime responded successfully.</returns>
        Task<bool> IsAvailableAsync(string workingDirectory, CancellationToken token = default);
    }
}
