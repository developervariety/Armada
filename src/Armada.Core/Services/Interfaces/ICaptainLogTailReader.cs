namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Reads a bounded tail of the log a running mission is writing. The read is shared with the
    /// writing process and takes no lock, and the reader writes nothing anywhere.
    /// </summary>
    public interface ICaptainLogTailReader
    {
        /// <summary>
        /// Read the last lines of the mission's live log.
        /// </summary>
        /// <param name="mission">The mission whose log to read.</param>
        /// <param name="lines">Maximum number of trailing lines to return.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The tail, newline separated; null when no log resolves for the mission.</returns>
        Task<string?> ReadTailAsync(Mission mission, int lines, CancellationToken token);
    }
}
