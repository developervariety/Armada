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
        /// Display name shown to other participants. Optional: when omitted the participant keeps
        /// its last board name, else takes the name of the credential the request authenticated with.
        /// </summary>
        public string? DisplayName { get; set; } = null;
    }
}
