namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Lifecycle of a battery run.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BatteryRunStatusEnum
    {
        /// <summary>
        /// Tasks are still being dispatched and graded.
        /// </summary>
        Running,

        /// <summary>
        /// Every task has a grade (a task grade may still be Failed or CouldNotRun).
        /// </summary>
        Completed,

        /// <summary>
        /// The run itself stopped on an error; the error is on the run.
        /// </summary>
        Failed,

        /// <summary>
        /// The Admiral restarted while the run was running.
        /// </summary>
        Interrupted
    }
}
