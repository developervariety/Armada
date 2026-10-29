namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// One resource listed by a vessel cleanup inventory, with its classification and the evidence behind it.
    /// </summary>
    public class VesselCleanupResource
    {
        /// <summary>
        /// Stable key used to select the resource for cleanup (for example dock:dck_x or branch:admiral|/repo|armada/x).
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Kind of resource.
        /// </summary>
        public VesselCleanupResourceTypeEnum ResourceType { get; set; } = VesselCleanupResourceTypeEnum.Dock;

        /// <summary>
        /// Classification.
        /// </summary>
        public VesselCleanupDispositionEnum Disposition { get; set; } = VesselCleanupDispositionEnum.Unknown;

        /// <summary>
        /// Human-readable reason for the classification.
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// Display identity: dock id, worktree path, or branch name.
        /// </summary>
        public string Identity { get; set; } = string.Empty;

        /// <summary>
        /// Worktree or repository path on the owning host, when known.
        /// </summary>
        public string? Path { get; set; } = null;

        /// <summary>
        /// Branch name, when known.
        /// </summary>
        public string? BranchName { get; set; } = null;

        /// <summary>
        /// Repository path the resource belongs to on the owning host, when known.
        /// </summary>
        public string? RepositoryPath { get; set; } = null;

        /// <summary>
        /// Harbor that owns the resource; null when it lives on the Admiral host.
        /// </summary>
        public string? HarborId { get; set; } = null;

        /// <summary>
        /// Owning dock, when known.
        /// </summary>
        public string? DockId { get; set; } = null;

        /// <summary>
        /// Owning (most relevant) mission, when known.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Status of the owning mission, when known.
        /// </summary>
        public MissionStatusEnum? MissionStatus { get; set; } = null;

        /// <summary>
        /// Owning voyage, when known.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Captain holding the resource, when known.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Measured size on disk in bytes; null when it could not be measured (never estimated).
        /// </summary>
        public long? SizeBytes { get; set; } = null;
    }
}
