namespace Armada.Server
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
        Failed
    }
}
