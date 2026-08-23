namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for coordination room participants (presence).
    /// </summary>
    public interface ICoordinationParticipantMethods
    {
        /// <summary>
        /// Insert or refresh a participant's presence in a room.
        /// </summary>
        Task<CoordinationParticipant> UpsertAsync(CoordinationParticipant participant, CancellationToken token = default);

        /// <summary>
        /// Enumerate participants in a room seen within the given number of minutes,
        /// most recently active first.
        /// </summary>
        Task<List<CoordinationParticipant>> EnumerateByRoomAsync(string coordinationRoomId, int activeWithinMinutes = 15, CancellationToken token = default);

        /// <summary>
        /// Enumerate all participants in a room regardless of last-seen age.
        /// </summary>
        Task<List<CoordinationParticipant>> EnumerateAllInRoomAsync(string coordinationRoomId, CancellationToken token = default);

        /// <summary>
        /// Delete presence rows in a room not seen since the given cutoff.
        /// </summary>
        Task PruneAsync(string coordinationRoomId, DateTime olderThanUtc, CancellationToken token = default);

        /// <summary>
        /// Delete all presence rows for a room.
        /// </summary>
        Task DeleteByRoomAsync(string coordinationRoomId, CancellationToken token = default);

        /// <summary>
        /// Read the most recent presence row for a participant key across every room,
        /// or null when the key has never been seen.
        /// </summary>
        Task<CoordinationParticipant?> ReadLatestByKeyAsync(string participantKey, CancellationToken token = default);
    }
}
