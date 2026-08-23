namespace Armada.Core.Models
{
    /// <summary>
    /// Request to create a coordination room.
    /// </summary>
    public class CoordinationRoomCreateRequest
    {
        /// <summary>
        /// URL-safe unique key for the room.
        /// </summary>
        public string Key { get; set; } = String.Empty;

        /// <summary>
        /// Display name.
        /// </summary>
        public string? Name { get; set; } = null;

        /// <summary>
        /// Optional description.
        /// </summary>
        public string? Description { get; set; } = null;
    }
}
