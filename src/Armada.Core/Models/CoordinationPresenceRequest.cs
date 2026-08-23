namespace Armada.Core.Models
{
    /// <summary>
    /// Request to refresh a participant's presence in a coordination room.
    /// </summary>
    public class CoordinationPresenceRequest
    {
        /// <summary>
        /// Stable participant key, unique within the room.
        /// </summary>
        public string? ParticipantKey { get; set; } = null;

        /// <summary>
        /// Display name shown to other participants.
        /// </summary>
        public string? DisplayName { get; set; } = null;
    }
}
