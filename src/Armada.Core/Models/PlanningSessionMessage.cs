namespace Armada.Core.Models
{
    /// <summary>
    /// A single message in a planning session transcript.
    /// </summary>
    public class PlanningSessionMessage
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
        /// Parent planning session identifier.
        /// </summary>
        public string PlanningSessionId { get; set; } = String.Empty;

        /// <summary>
        /// Tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Owning user identifier.
        /// </summary>
        public string? UserId { get; set; } = null;

        /// <summary>
        /// Message role.
        /// Expected values include User, Assistant, and System.
        /// </summary>
        public string Role { get; set; } = "User";

        /// <summary>
        /// Monotonic message sequence within the session.
        /// </summary>
        public int Sequence { get; set; } = 0;

        /// <summary>
        /// Message content.
        /// </summary>
        public string Content { get; set; } = String.Empty;

        /// <summary>
        /// Whether this message was selected for dispatch.
        /// </summary>
        public bool IsSelectedForDispatch { get; set; } = false;

        /// <summary>
        /// Creation timestamp in UTC.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last update timestamp in UTC.
        /// </summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Per-turn timing and token statistics for an assistant message (time to first token,
        /// streaming time, tokens per second). Populated live when a turn completes and delivered over
        /// the WebSocket; it is not persisted, so it is null for messages reloaded from storage.
        /// </summary>
        public CaptainChatMetrics? Metrics { get; set; } = null;

        #endregion

        #region Private-Members

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.PlanningSessionMessageIdPrefix, 24);

        #endregion
    }
}
