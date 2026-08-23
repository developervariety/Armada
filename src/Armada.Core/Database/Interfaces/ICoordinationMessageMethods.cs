namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for coordination room messages.
    /// </summary>
    public interface ICoordinationMessageMethods
    {
        /// <summary>
        /// Create a coordination message.
        /// </summary>
        Task<CoordinationMessage> CreateAsync(CoordinationMessage message, CancellationToken token = default);

        /// <summary>
        /// Read a coordination message by identifier.
        /// </summary>
        Task<CoordinationMessage?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Update a coordination message.
        /// </summary>
        Task<CoordinationMessage> UpdateAsync(CoordinationMessage message, CancellationToken token = default);

        /// <summary>
        /// Delete a coordination message by identifier.
        /// </summary>
        Task DeleteAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Delete all coordination messages for a room.
        /// </summary>
        Task DeleteByRoomAsync(string coordinationRoomId, CancellationToken token = default);

        /// <summary>
        /// Enumerate messages in a room in chronological order. When afterUtc is supplied,
        /// only messages created strictly after that instant are returned.
        /// </summary>
        Task<List<CoordinationMessage>> EnumerateByRoomAsync(string coordinationRoomId, DateTime? afterUtc = null, int limit = 200, CancellationToken token = default);

        /// <summary>
        /// Enumerate a room as a specific participant sees it: broadcast notes plus notes
        /// addressed to that participant, chronological. participantKey null behaves as
        /// EnumerateByRoomAsync (everything).
        /// </summary>
        Task<List<CoordinationMessage>> EnumerateVisibleToAsync(string coordinationRoomId, string? participantKey, DateTime? afterUtc = null, int limit = 200, CancellationToken token = default);

        /// <summary>
        /// Enumerate messages that reference a voyage, newest first. When afterUtc is
        /// supplied, only messages created strictly after that instant are returned.
        /// </summary>
        Task<List<CoordinationMessage>> EnumerateByVoyageAsync(string voyageId, DateTime? afterUtc = null, int limit = 20, CancellationToken token = default);
    }
}
