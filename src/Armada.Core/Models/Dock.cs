namespace Armada.Core.Models
{
    using System.Text.Json.Serialization;

    /// <summary>
    /// A git worktree provisioned for a captain.
    /// </summary>
    public class Dock
    {
        #region Public-Members

        /// <summary>
        /// Unique identifier.
        /// </summary>
        public string Id
        {
            get => _Id;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(Id));
                _Id = value;
            }
        }

        /// <summary>
        /// Tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Owning user identifier.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>Stable assignment attempt that owns this dock provisioning intent; null for legacy docks.</summary>
        public string? ProvisionAttemptId { get; set; } = null;

        /// <summary>
        /// Vessel identifier this dock is for.
        /// </summary>
        public string VesselId
        {
            get => _VesselId;
            set
            {
                if (String.IsNullOrEmpty(value)) throw new ArgumentNullException(nameof(VesselId));
                _VesselId = value;
            }
        }

        /// <summary>
        /// Captain identifier currently using this dock.
        /// </summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>
        /// Local filesystem path, or an opaque Harbor address when HostCheckout is present.
        /// </summary>
        public string? WorktreePath { get; set; } = null;

        /// <summary>Durable Harbor-owned checkout identity. A non-null incomplete binding must fail closed.</summary>
        public HarborCheckoutBinding? HostCheckout { get; set; }

        /// <summary>Durable in-progress Harbor lifecycle intent with pinned head and replay phase.</summary>
        public HarborCheckoutLifecycleIntent? HostCheckoutLifecycle { get; set; }

        /// <summary>
        /// Branch name checked out in this worktree.
        /// </summary>
        public string? BranchName { get; set; } = null;

        /// <summary>
        /// Bounded provisioning evidence. Null when absent, invalid or from an unsupported version.
        /// </summary>
        public DockGitAnchorSnapshot? GitAnchorsSnapshot { get; set; }

        /// <summary>
        /// Whether the dock is active and usable.
        /// </summary>
        public bool Active { get; set; } = true;

        /// <summary>
        /// Creation timestamp in UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last update timestamp in UTC.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.DockIdPrefix, 24);
        private string _VesselId = "";

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public Dock()
        {
        }

        /// <summary>
        /// Instantiate with vessel identifier.
        /// </summary>
        /// <param name="vesselId">Vessel identifier.</param>
        public Dock(string vesselId)
        {
            VesselId = vesselId;
        }

        #endregion
    }
}
