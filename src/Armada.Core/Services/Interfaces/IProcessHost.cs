namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Abstracts OS-level process spawning for testability. The implementation launches the
    /// process, writes the stdin payload, and returns immediately without blocking on exit.
    /// </summary>
    public interface IProcessHost
    {
        /// <summary>
        /// Starts the process described by <paramref name="request"/>, writes the stdin payload,
        /// and returns a result containing the process ID. Does not block on process exit.
        /// </summary>
        Task<ProcessSpawnResult> SpawnDetachedAsync(ProcessSpawnRequest request, CancellationToken token);
    }
}
