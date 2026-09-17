namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// One screening pass over a captain log tail. A pass reads the supplied context and returns
    /// what it found; it writes no record, reads no file, and has no authority over the mission.
    /// Several passes run over the same context, so a pass reports only its own classes.
    /// </summary>
    public interface ICaptainLogScreenPass
    {
        /// <summary>
        /// Stable name of this pass, used in the screen's own log lines and outcome records.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Evaluate one mission's log tail.
        /// </summary>
        /// <param name="context">The bounded tail and the identity of the work it came from.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The findings, empty when the tail is clean. Never null.</returns>
        Task<IReadOnlyList<LogScreenFinding>> EvaluateAsync(LogScreenContext context, CancellationToken token);
    }
}
