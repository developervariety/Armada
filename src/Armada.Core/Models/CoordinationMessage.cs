namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// A single note posted in a coordination room.
    /// </summary>
    public class CoordinationMessage
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
        /// The kind of participant that authored this message.
        /// </summary>
        public CoordinationAuthorTypeEnum AuthorType { get; set; } = CoordinationAuthorTypeEnum.Operator;

        /// <summary>
        /// Stable identifier of the authoring participant, for example an operator
        /// session name or captain identifier. Null for system messages.
        /// </summary>
        public string? AuthorId { get; set; } = null;

        /// <summary>
        /// Display name of the author at posting time.
        /// </summary>
        public string AuthorName { get; set; } = String.Empty;

        /// <summary>
        /// Message content.
        /// </summary>
        public string Content { get; set; } = String.Empty;

        /// <summary>
        /// Related voyage identifier, when the note refers to a voyage.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Related mission identifier, when the note refers to a mission.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Related vessel identifier, when the note refers to a vessel.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Related incident identifier, when the note refers to an incident.
        /// </summary>
        public string? IncidentId { get; set; } = null;

        /// <summary>
        /// Optional participant key this note is addressed to. Null means broadcast to
        /// the whole room. Addressed notes are still visible to everyone; the key marks
        /// them as work directed at one participant.
        /// </summary>
        public string? ToParticipantKey { get; set; } = null;

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

        private string _Id = Constants.IdGenerator.GenerateKSortable(Constants.CoordinationMessageIdPrefix, 24);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public CoordinationMessage()
        {
        }

        #endregion
    }
}
