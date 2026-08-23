namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// A reservation that one participant holds against a vessel or objective so
    /// concurrent operator sessions do not dispatch the same work. Claims expire;
    /// heartbeats keep a live session's claims alive.
    /// </summary>
    public class CoordinationClaim
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
        /// Stable key of the holding participant, unique within the room.
        /// </summary>
        public string ParticipantKey { get; set; } = String.Empty;

        /// <summary>
        /// Display name of the holder at claim time.
        /// </summary>
        public string DisplayName { get; set; } = String.Empty;

        /// <summary>
        /// What kind of record the claim reserves.
        /// </summary>
        public CoordinationClaimSubjectEnum SubjectType { get; set; } = CoordinationClaimSubjectEnum.Vessel;

        /// <summary>
        /// Identifier of the reserved record.
        /// </summary>
        public string SubjectId { get; set; } = String.Empty;

        /// <summary>
        /// Free-text note about the intended work.
        /// </summary>
        public string? Note { get; set; } = null;

        /// <summary>
        /// Claim status.
        /// </summary>
        public CoordinationClaimStatusEnum Status { get; set; } = CoordinationClaimStatusEnum.Active;

        /// <summary>
        /// When the claim lapses unless refreshed. Active claims are those with
        /// status Active and an expiry in the future.
        /// </summary>
        public DateTime ExpiresUtc { get; set; } = DateTime.UtcNow.AddHours(4);

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

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.CoordinationClaimIdPrefix, 24);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CoordinationClaim()
        {
        }

        #endregion
    }
}
