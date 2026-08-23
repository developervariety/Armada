namespace Armada.Core.Database.SqlServer.Implementations
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// SqlServer stub for coordination room operations.
    /// </summary>
    public class CoordinationRoomMethods : ICoordinationRoomMethods
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="driver">Database driver.</param>
        public CoordinationRoomMethods(SqlServerDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
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
