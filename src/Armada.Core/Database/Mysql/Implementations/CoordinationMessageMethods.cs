namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// Mysql stub for coordination message operations.
    /// </summary>
    public class CoordinationMessageMethods : ICoordinationMessageMethods
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="connectionString">Database connection string.</param>
        public CoordinationMessageMethods(string connectionString)
        {
        }

        /// <inheritdoc />
        public Task<CoordinationMessage> CreateAsync(CoordinationMessage message, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task<CoordinationMessage?> ReadAsync(string id, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task<CoordinationMessage> UpdateAsync(CoordinationMessage message, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task DeleteAsync(string id, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task DeleteByRoomAsync(string coordinationRoomId, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task<List<CoordinationMessage>> EnumerateByRoomAsync(string coordinationRoomId, DateTime? afterUtc = null, int limit = 200, CancellationToken token = default) => throw NotSupported();

        private static NotSupportedException NotSupported()
        {
            return new NotSupportedException("Coordination rooms are currently implemented for SQLite- and PostgreSQL-backed Armada deployments.");
        }
    }
}
