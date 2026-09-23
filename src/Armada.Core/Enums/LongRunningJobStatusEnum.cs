namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Enumerates the lifecycle states of a process-local long-running job.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum LongRunningJobStatusEnum
    {
        /// <summary>
        /// The job was accepted and is waiting for background execution to begin.
        /// </summary>
        Accepted,

        /// <summary>
        /// The job is currently executing.
        /// </summary>
        Running,

        /// <summary>
        /// The job completed successfully.
        /// </summary>
        Succeeded,

        /// <summary>
        /// The job completed with an error.
        /// </summary>
        Failed,

        /// <summary>
        /// The admiral process that accepted the job stopped before the job finished (a restart or a
        /// crash), so the operation did not complete. Recorded from the durable job journal at the next
        /// start; the job never resumes by itself.
        /// </summary>
        Lost
    }
}
