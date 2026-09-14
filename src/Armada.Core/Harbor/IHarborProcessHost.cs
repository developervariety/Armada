namespace Armada.Core.Harbor
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Runs a runtime's launch plan on a Harbor runner instead of as a local process. The returned identifier is a
    /// synthetic process identifier registered with <see cref="ProcessSupervisor"/>, so liveness, stop, stall
    /// detection and recovery treat the job like a local process.
    /// </summary>
    public interface IHarborProcessHost
    {
        /// <summary>Launch a job and deliver its output and exit to the events.</summary>
        /// <param name="launch">Launch plan and ownership.</param>
        /// <param name="events">Receives output and the exit.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Synthetic process identifier.</returns>
        /// <exception cref="HarborLaunchException">The launch was refused.</exception>
        Task<int> LaunchAsync(HarborProcessLaunch launch, IHarborProcessEvents events, CancellationToken token = default);
    }

    /// <summary>Receives the process view of a Harbor job, in the order the runner reported it.</summary>
    public interface IHarborProcessEvents
    {
        /// <summary>One output chunk.</summary>
        /// <param name="processId">Synthetic process identifier.</param>
        /// <param name="stream">Stream that produced the chunk.</param>
        /// <param name="data">Chunk text.</param>
        void OnOutput(int processId, HarborOutputStreamEnum stream, string data);

        /// <summary>The job ended.</summary>
        /// <param name="processId">Synthetic process identifier.</param>
        /// <param name="exitCode">Exit code the runner reported, or null when the job did not exit normally.</param>
        /// <param name="failureReason">Named reason when the job failed or was lost.</param>
        void OnExited(int processId, int? exitCode, string? failureReason);
    }
}
