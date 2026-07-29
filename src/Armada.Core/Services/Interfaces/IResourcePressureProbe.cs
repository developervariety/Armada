namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Probe that reports host/container resource pressure at a point in time.
    /// Injectable so tests can substitute a deterministic probe.
    /// </summary>
    public interface IResourcePressureProbe
    {
        /// <summary>
        /// Take a resource-pressure snapshot.
        /// </summary>
        /// <returns>Snapshot of available host/container memory.</returns>
        ResourcePressureSnapshot Probe();
    }
}