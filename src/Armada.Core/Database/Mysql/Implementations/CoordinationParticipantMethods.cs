namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// Mysql stub for coordination participant operations.
    /// </summary>
    public class CoordinationParticipantMethods : ICoordinationParticipantMethods
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="connectionString">Database connection string.</param>
        public CoordinationParticipantMethods(string connectionString)
        {
        }

        /// <inheritdoc />
        public Task<CoordinationParticipant> UpsertAsync(CoordinationParticipant participant, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task<List<CoordinationParticipant>> EnumerateByRoomAsync(string coordinationRoomId, int activeWithinMinutes = 15, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task<List<CoordinationParticipant>> EnumerateAllInRoomAsync(string coordinationRoomId, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task PruneAsync(string coordinationRoomId, DateTime olderThanUtc, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task DeleteByRoomAsync(string coordinationRoomId, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task<CoordinationParticipant?> ReadLatestByKeyAsync(string participantKey, CancellationToken token = default) => throw NotSupported();

        private static NotSupportedException NotSupported()
        {
            return new NotSupportedException("Coordination rooms are currently implemented for SQLite- and PostgreSQL-backed Armada deployments.");
        }
    }
}
