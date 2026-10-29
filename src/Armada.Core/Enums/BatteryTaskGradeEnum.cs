namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// The grade of one battery task.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BatteryTaskGradeEnum
    {
        /// <summary>
        /// The grader accepted the work.
        /// </summary>
        Passed,

        /// <summary>
        /// The grader rejected the work, or the mission failed, was cancelled or timed out.
        /// </summary>
        Failed,

        /// <summary>
        /// The task could not be graded: the host lacks a tool, the fixture could not be reset, another captain ran
        /// the mission, or the worktree is gone. It says nothing about the captain and never counts as a failure.
        /// </summary>
        CouldNotRun
    }
}
