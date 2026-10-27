namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome of one battery task attempt.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BatteryTaskOutcomeEnum
    {
        /// <summary>
        /// The grader accepted the work.
        /// </summary>
        Passed,

        /// <summary>
        /// The grader rejected the work, or the mission failed.
        /// </summary>
        Failed,

        /// <summary>
        /// The task could not run (for example the Harbor lacks the pack's toolchain); never counted as a failure.
        /// </summary>
        CouldNotRun,

        /// <summary>
        /// Waiting for the evaluator captain (rubric-graded tasks); the raw output is stored.
        /// </summary>
        PendingEvaluation
    }
}
