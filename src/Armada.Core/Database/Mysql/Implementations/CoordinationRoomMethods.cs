namespace Armada.Core.Database.Mysql.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;

    /// <summary>
    /// Mysql stub for coordination room operations.
    /// </summary>
    public class CoordinationRoomMethods : ICoordinationRoomMethods
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="connectionString">Database connection string.</param>
        public CoordinationRoomMethods(string connectionString)
        {
        }

        /// <inheritdoc />
        public Task<CoordinationRoom> CreateAsync(CoordinationRoom room, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task<CoordinationRoom?> ReadAsync(string id, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task<CoordinationRoom?> ReadByKeyAsync(string key, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task<CoordinationRoom> UpdateAsync(CoordinationRoom room, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task DeleteAsync(string id, CancellationToken token = default) => throw NotSupported();

        /// <inheritdoc />
        public Task<List<CoordinationRoom>> EnumerateAsync(CancellationToken token = default) => throw NotSupported();

        private static NotSupportedException NotSupported()
        {
            return new NotSupportedException("Coordination rooms are currently implemented for SQLite- and PostgreSQL-backed Armada deployments.");
        }
    }
}
