namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Strategy for selecting which ref of a declared sibling repository is checked out
    /// into a dock so the consumer's cross-repo source probes resolve against a
    /// branch-compatible copy of the sibling.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum SiblingBranchStrategyEnum
    {
        /// <summary>
        /// Prefer the sibling branch whose name matches the dock's mission branch so the
        /// sibling tracks the same feature work; fall back to the sibling's default branch
        /// when no matching branch exists. This is the default.
        /// </summary>
        [EnumMember(Value = "MatchBranchElseDefault")]
        MatchBranchElseDefault,

        /// <summary>
        /// Always check out the sibling's default branch, ignoring the dock's mission branch.
        /// </summary>
        [EnumMember(Value = "DefaultOnly")]
        DefaultOnly
    }
}
