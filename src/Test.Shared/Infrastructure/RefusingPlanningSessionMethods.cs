namespace Test.Shared.Infrastructure
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Planning-session storage that refuses every call with <see cref="NotSupportedException"/>, the contract a
    /// database provider uses for a record type it does not store. Suites swap it in to prove that callers treat
    /// such a provider as having no planning sessions rather than as a failure.
    /// </summary>
    public class RefusingPlanningSessionMethods : IPlanningSessionMethods
    {
        /// <summary>
        /// Message carried by every refusal.
        /// </summary>
        public const string Message = "This database provider does not store planning sessions.";

        /// <inheritdoc />
        public Task<PlanningSession> CreateAsync(PlanningSession session, CancellationToken token = default) => throw Refuse();
        /// <inheritdoc />
        public Task<PlanningSession?> ReadAsync(string id, CancellationToken token = default) => throw Refuse();
        /// <inheritdoc />
        public Task<PlanningSession> UpdateAsync(PlanningSession session, CancellationToken token = default) => throw Refuse();
        /// <inheritdoc />
        public Task DeleteAsync(string id, CancellationToken token = default) => throw Refuse();
        /// <inheritdoc />
        public Task<List<PlanningSession>> EnumerateAsync(CancellationToken token = default) => throw Refuse();
        /// <inheritdoc />
        public Task<List<PlanningSession>> EnumerateByCaptainAsync(string captainId, CancellationToken token = default) => throw Refuse();
        /// <inheritdoc />
        public Task<List<PlanningSession>> EnumerateByStatusAsync(PlanningSessionStatusEnum status, CancellationToken token = default) => throw Refuse();
        /// <inheritdoc />
        public Task<PlanningSession?> ReadAsync(string tenantId, string id, CancellationToken token = default) => throw Refuse();
        /// <inheritdoc />
        public Task<List<PlanningSession>> EnumerateAsync(string tenantId, CancellationToken token = default) => throw Refuse();
        /// <inheritdoc />
        public Task<PlanningSession?> ReadAsync(string tenantId, string userId, string id, CancellationToken token = default) => throw Refuse();
        /// <inheritdoc />
        public Task<List<PlanningSession>> EnumerateAsync(string tenantId, string userId, CancellationToken token = default) => throw Refuse();

        private static NotSupportedException Refuse()
        {
            return new NotSupportedException(Message);
        }
    }
}
