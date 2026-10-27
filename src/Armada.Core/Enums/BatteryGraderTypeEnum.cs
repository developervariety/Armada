namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// How a battery task is graded (docs/MODEL_HETEROGENEITY.md, Part 2).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BatteryGraderTypeEnum
    {
        /// <summary>
        /// Run commands (build, tests) in the task's worktree; every command must exit with its expected code.
        /// </summary>
        Command,

        /// <summary>
        /// Compare files in the worktree byte for byte with expected files (git blob hashes).
        /// </summary>
        ByteComparison,

        /// <summary>
        /// Match the captain's findings against a seeded set of known defects; findings that match none are false positives.
        /// </summary>
        SeededSet,

        /// <summary>
        /// Run the commands, then apply each mutant to the source and require the commands to fail (the tests kill it).
        /// </summary>
        Mutation,

        /// <summary>
        /// Graded later by an evaluator captain against a rubric; recorded as pending evaluation with the raw output.
        /// </summary>
        Rubric
    }
}
