namespace Armada.Core.Database.SqlServer.Implementations
{
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// SQL Server stub for planning session operations.
    /// </summary>
    public class PlanningSessionMethods : IPlanningSessionMethods
    {
        /// <summary>
        /// Instantiate.
        /// </summary>
        public PlanningSessionMethods(SqlServerDatabaseDriver driver, DatabaseSettings settings, LoggingModule logging)
        {
        }

        public Task<PlanningSession> CreateAsync(PlanningSession session, CancellationToken token = default) => throw NotSupported();
        public Task DeleteAsync(string id, CancellationToken token = default) => throw NotSupported();
        public Task<List<PlanningSession>> EnumerateAsync(CancellationToken token = default) => throw NotSupported();
        public Task<List<PlanningSession>> EnumerateAsync(string tenantId, CancellationToken token = default) => throw NotSupported();
        public Task<List<PlanningSession>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default) => throw NotSupported();
        public Task<List<PlanningSession>> EnumerateByCaptainAsync(string captainId, CancellationToken token = default) => throw NotSupported();
        public Task<List<PlanningSession>> EnumerateByStatusAsync(PlanningSessionStatusEnum status, CancellationToken token = default) => throw NotSupported();
        public Task<PlanningSession?> ReadAsync(string id, CancellationToken token = default) => throw NotSupported();
        public Task<PlanningSession?> ReadAsync(string tenantId, string id, CancellationToken token = default) => throw NotSupported();
        public Task<PlanningSession?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default) => throw NotSupported();
        public Task<PlanningSession> UpdateAsync(PlanningSession session, CancellationToken token = default) => throw NotSupported();

        private static NotSupportedException NotSupported()
        {
            return new NotSupportedException("Planning sessions are currently implemented for SQLite-backed Armada deployments.");
        }
    }
}
