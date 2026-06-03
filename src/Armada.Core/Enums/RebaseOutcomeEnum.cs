namespace Armada.Core.Enums
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome from rebasing a mission branch onto a target branch.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RebaseOutcomeEnum
    {
        /// <summary>
        /// Rebase completed cleanly.
        /// </summary>
        Clean,

        /// <summary>
        /// Rebase found content conflicts and was aborted.
        /// </summary>
        Conflict,

        /// <summary>
        /// Rebase failed for a non-conflict reason.
        /// </summary>
        Error
    }
}
