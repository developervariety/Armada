namespace Armada.Core.Enums
{
    using System.Runtime.Serialization;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Lifecycle state of a dock (git worktree). Makes dock occupancy authoritative on the dock
    /// row itself rather than a predicate derived across the dock, captain, and mission tables,
    /// so a dock can be reliably detected as free, leased, or in need of reclamation.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DockStateEnum
    {
        /// <summary>
        /// Dock exists and is not leased to any captain; available for assignment or cleanup.
        /// </summary>
        [EnumMember(Value = "Available")]
        Available,

        /// <summary>
        /// Dock is being provisioned (worktree creation in progress).
        /// </summary>
        [EnumMember(Value = "Provisioning")]
        Provisioning,

        /// <summary>
        /// Dock is leased to a captain for active work. The lease is held until
        /// <c>LeaseExpiresUtc</c> and must be renewed while the captain is alive.
        /// </summary>
        [EnumMember(Value = "Leased")]
        Leased,

        /// <summary>
        /// Dock is being reclaimed (worktree removal in progress).
        /// </summary>
        [EnumMember(Value = "Reclaiming")]
        Reclaiming,

        /// <summary>
        /// Dock reclamation failed and the worktree may still exist on disk; needs operator
        /// attention or repair rather than silent reuse.
        /// </summary>
        [EnumMember(Value = "Failed")]
        Failed
    }
}
