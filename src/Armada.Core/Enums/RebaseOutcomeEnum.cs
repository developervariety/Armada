namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome of an attempt to rebase a branch onto another branch. Distinguishes a clean
    /// rebase (target-branch drift only) from a genuine content conflict that needs human help.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RebaseOutcomeEnum
    {
        /// <summary>
        /// The rebase applied cleanly; the branch now sits on top of the target tip.
        /// </summary>
        Clean,

        /// <summary>
        /// The rebase produced content conflicts and was aborted, leaving the repository clean.
        /// A genuine conflict that needs human intervention, not a drift-only failure.
        /// </summary>
        Conflict,

        /// <summary>
        /// The rebase could not be performed for a non-conflict reason (missing branch, git error).
        /// Any in-progress rebase was aborted so the repository is left in a clean state.
        /// </summary>
        Error
    }
}
