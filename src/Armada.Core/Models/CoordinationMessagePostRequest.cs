namespace Armada.Core.Models
{
    using Armada.Core.Enums;
    /// <summary>
    /// Request to post a note to a coordination room.
    /// </summary>
    public class CoordinationMessagePostRequest
    {
        /// <summary>
        /// The kind of participant posting the note. Defaults to Operator.
        /// </summary>
        public CoordinationAuthorTypeEnum? AuthorType { get; set; } = null;

        /// <summary>
        /// Stable identifier of the authoring participant.
        /// </summary>
        public string? AuthorId { get; set; } = null;

        /// <summary>
        /// Display name of the author.
        /// </summary>
        public string? AuthorName { get; set; } = null;

        /// <summary>
        /// Message content.
        /// </summary>
        public string? Content { get; set; } = null;

        /// <summary>
        /// Optional related voyage identifier.
        /// </summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>
        /// Optional related mission identifier.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Optional related vessel identifier.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Optional related incident identifier.
        /// </summary>
        public string? IncidentId { get; set; } = null;
    }
}
