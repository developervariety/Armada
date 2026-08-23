namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for coordination rooms.
    /// </summary>
    public interface ICoordinationRoomMethods
    {
        /// <summary>
        /// Create a coordination room.
        /// </summary>
        Task<CoordinationRoom> CreateAsync(CoordinationRoom room, CancellationToken token = default);

        /// <summary>
        /// Read a coordination room by identifier.
        /// </summary>
        Task<CoordinationRoom?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Read a coordination room by its unique key.
        /// </summary>
        Task<CoordinationRoom?> ReadByKeyAsync(string key, CancellationToken token = default);

        /// <summary>
        /// Update a coordination room.
        /// </summary>
        Task<CoordinationRoom> UpdateAsync(CoordinationRoom room, CancellationToken token = default);

        /// <summary>
        /// Delete a coordination room by identifier.
        /// </summary>
        Task DeleteAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Enumerate all coordination rooms ordered by most recent activity.
        /// </summary>
        Task<List<CoordinationRoom>> EnumerateAsync(CancellationToken token = default);
    }
}
