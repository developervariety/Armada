namespace Armada.Core.Harbor
{
    /// <summary>
    /// Receives the lifecycle of one Harbor job. Notifications for a job are delivered one at a time, in the order the
    /// coordinator accepted the events, and never while the coordinator's lock is held.
    /// </summary>
    public interface IHarborJobObserver
    {
        /// <summary>The owning runner reported that the process started.</summary>
        /// <param name="job">Job view after the start.</param>
        void OnStarted(HarborJobSnapshot job);

        /// <summary>The owning runner reported one output chunk, accepted in sequence.</summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="output">Accepted chunk.</param>
        void OnOutput(string jobId, HarborOutput output);

        /// <summary>The job reached a terminal state: Exited, Failed or Lost.</summary>
        /// <param name="job">Terminal job view, carrying the exit code or the named failure reason.</param>
        void OnFinished(HarborJobSnapshot job);
    }
}
