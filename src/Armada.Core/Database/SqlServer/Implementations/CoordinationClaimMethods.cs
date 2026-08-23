namespace Armada.Core.Database.SqlServer.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// SqlServer stub for coordination claim operations.
    /// </summary>
    public class CoordinationClaimMethods : ICoordinationClaimMethods
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="driver">Database driver.</param>
        /// <param name="settings">Database settings.</param>
        /// <param name="logging">Logging module.</param>
        public CoordinationClaimMethods(SqlServerDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
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
