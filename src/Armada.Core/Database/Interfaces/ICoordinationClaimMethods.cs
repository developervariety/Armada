namespace Armada.Core.Database.Interfaces
{
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for coordination claims (work reservations).
    /// </summary>
    public interface ICoordinationClaimMethods
    {
        /// <summary>
        /// Create a coordination claim.
        /// </summary>
        Task<CoordinationClaim> CreateAsync(CoordinationClaim claim, CancellationToken token = default);

        /// <summary>
        /// Read a coordination claim by identifier.
        /// </summary>
        Task<CoordinationClaim?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>
        /// Update a coordination claim.
        /// </summary>
        Task<CoordinationClaim> UpdateAsync(CoordinationClaim claim, CancellationToken token = default);

        /// <summary>
        /// Enumerate active claims - status Active with an expiry in the future -
        /// optionally narrowed by subject type and identifier.
        /// </summary>
        Task<List<CoordinationClaim>> EnumerateActiveAsync(CoordinationClaimSubjectEnum? subjectType = null, string? subjectId = null, CancellationToken token = default);

        /// <summary>
        /// Extend the expiry of every active claim a participant holds in a room,
        /// returning the number of claims extended.
        /// </summary>
        Task<int> ExtendActiveForParticipantAsync(string coordinationRoomId, string participantKey, DateTime newExpiresUtc, CancellationToken token = default);
    }
}
