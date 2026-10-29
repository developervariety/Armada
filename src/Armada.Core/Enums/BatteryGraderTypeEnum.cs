namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// How a battery task is graded. See battery/README.md.
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
        /// Run the commands, then apply each mutant to the source and require the commands to fail (the tests kill it).
        /// </summary>
        Mutation
    }
}
