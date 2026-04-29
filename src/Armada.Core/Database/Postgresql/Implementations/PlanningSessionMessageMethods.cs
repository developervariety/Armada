namespace Armada.Core.Database.Postgresql.Implementations
{
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// PostgreSQL stub for planning session message operations.
    /// </summary>
    public class PlanningSessionMessageMethods : IPlanningSessionMessageMethods
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        public PlanningSessionMessageMethods(PostgresqlDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
        }

        public Task<PlanningSessionMessage> CreateAsync(PlanningSessionMessage message, CancellationToken token = default) => throw NotSupported();
        public Task DeleteAsync(string id, CancellationToken token = default) => throw NotSupported();
        public Task DeleteBySessionAsync(string planningSessionId, CancellationToken token = default) => throw NotSupported();
        public Task<List<PlanningSessionMessage>> EnumerateBySessionAsync(string planningSessionId, CancellationToken token = default) => throw NotSupported();
        public Task<PlanningSessionMessage?> ReadAsync(string id, CancellationToken token = default) => throw NotSupported();
        public Task<PlanningSessionMessage> UpdateAsync(PlanningSessionMessage message, CancellationToken token = default) => throw NotSupported();

        private static NotSupportedException NotSupported()
        {
            return new NotSupportedException("Planning sessions are currently implemented for SQLite-backed Armada deployments.");
        }
    }
}
