namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Outcome of saving a dock's uncommitted work to a recover branch.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DockWorkSnapshotStatusEnum
    {
        /// <summary>
        /// A new recover branch holds the dock's work.
        /// </summary>
        [EnumMember(Value = "Saved")]
        Saved,

        /// <summary>
        /// The newest recover branch for the mission already holds the same work.
        /// </summary>
        [EnumMember(Value = "AlreadySaved")]
        AlreadySaved,

        /// <summary>
        /// The dock has no work beyond its HEAD commit, so nothing was saved.
        /// </summary>
        [EnumMember(Value = "NoChanges")]
        NoChanges,

        /// <summary>
        /// The work could not be saved; the message names why.
        /// </summary>
        [EnumMember(Value = "Failed")]
        Failed
    }
}
