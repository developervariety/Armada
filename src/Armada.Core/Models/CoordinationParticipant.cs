namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Presence record for a participant in a coordination room. One row per
    /// participant per room, refreshed by heartbeats.
    /// </summary>
    public class CoordinationParticipant
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
        /// Parent coordination room identifier.
        /// </summary>
        public string CoordinationRoomId { get; set; } = String.Empty;

        /// <summary>
        /// Tenant identifier.
        /// </summary>
        public string? TenantId { get; set; } = null;

        /// <summary>
        /// Stable participant key, unique within the room, for example an operator
        /// session name or captain identifier.
        /// </summary>
        public string ParticipantKey { get; set; } = String.Empty;

        /// <summary>
        /// Display name shown to other participants.
        /// </summary>
        public string DisplayName { get; set; } = String.Empty;

        /// <summary>
        /// Timestamp of the most recent heartbeat or message in UTC.
        /// </summary>
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

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

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.CoordinationParticipantIdPrefix, 24);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CoordinationParticipant()
        {
        }

        #endregion
    }
}
