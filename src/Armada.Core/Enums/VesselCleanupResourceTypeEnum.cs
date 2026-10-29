namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Kind of resource listed by a vessel cleanup inventory.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum VesselCleanupResourceTypeEnum
    {
        /// <summary>
        /// A dock record and its worktree directory.
        /// </summary>
        [EnumMember(Value = "Dock")]
        Dock,

        /// <summary>
        /// A git worktree registered in a vessel repository that no dock record owns.
        /// </summary>
        [EnumMember(Value = "Worktree")]
        Worktree,

        /// <summary>
        /// An Armada-managed local branch (armada/ prefix) in a vessel repository.
        /// </summary>
        [EnumMember(Value = "Branch")]
        Branch,

        /// <summary>
        /// A vessel repository that could not be inspected (for example on a disconnected Harbor). Never cleaned.
        /// </summary>
        [EnumMember(Value = "Repository")]
        Repository
    }
}
