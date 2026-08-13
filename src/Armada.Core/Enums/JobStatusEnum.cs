namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Lifecycle status of a background job.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum JobStatusEnum
    {
        /// <summary>
        /// The job is enqueued and waiting to be claimed by the coordinator.
        /// </summary>
        [EnumMember(Value = "Queued")]
        Queued,

        /// <summary>
        /// The job has been claimed and is executing.
        /// </summary>
        [EnumMember(Value = "Running")]
        Running,

        /// <summary>
        /// The job completed successfully.
        /// </summary>
        [EnumMember(Value = "Succeeded")]
        Succeeded,

        /// <summary>
        /// The job failed.
        /// </summary>
        [EnumMember(Value = "Failed")]
        Failed,

        /// <summary>
        /// The job was cancelled before completing.
        /// </summary>
        [EnumMember(Value = "Cancelled")]
        Cancelled
    }
}
