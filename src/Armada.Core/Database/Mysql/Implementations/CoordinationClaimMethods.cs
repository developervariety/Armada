namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Mysql stub for coordination claim operations.
    /// </summary>
    public class CoordinationClaimMethods : ICoordinationClaimMethods
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="connectionString">Database connection string.</param>
        public CoordinationClaimMethods(string connectionString)
        {
        }

        /// <inheritdoc />
        public Task<CoordinationClaim> CreateAsync(CoordinationClaim claim, CancellationToken token = default) => throw NotSupported();
        /// <inheritdoc />
        public Task<CoordinationClaim?> ReadAsync(string id, CancellationToken token = default) => throw NotSupported();
        /// <inheritdoc />
        public Task<CoordinationClaim> UpdateAsync(CoordinationClaim claim, CancellationToken token = default) => throw NotSupported();
        /// <inheritdoc />
        public Task<List<CoordinationClaim>> EnumerateActiveAsync(CoordinationClaimSubjectEnum? subjectType = null, string? subjectId = null, CancellationToken token = default) => throw NotSupported();
        /// <inheritdoc />
        public Task<int> ExtendActiveForParticipantAsync(string coordinationRoomId, string participantKey, DateTime newExpiresUtc, CancellationToken token = default) => throw NotSupported();

        private static NotSupportedException NotSupported()
        {
            return new NotSupportedException("Coordination claims are currently implemented for SQLite- and PostgreSQL-backed Armada deployments.");
        }
    }
}
