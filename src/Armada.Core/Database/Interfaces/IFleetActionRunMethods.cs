namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Exact-owner persistence for durable fleet action runs.</summary>
    public interface IFleetActionRunMethods
    {
        /// <summary>Create a run, or read the run that already owns this actor request ID.</summary>
        Task<FleetActionRun> CreateOrReadAsync(FleetActionRun run, CancellationToken token = default);
        /// <summary>Read a prior run by exact actor and request identifier.</summary>
        Task<FleetActionRun?> ReadByRequestIdAsync(string tenantId, string userId, string requestId, CancellationToken token = default);
        /// <summary>Read one run for its exact owner.</summary>
        Task<FleetActionRun?> ReadAsync(string tenantId, string userId, string runId, CancellationToken token = default);
        /// <summary>List runs for one exact owner, newest first.</summary>
        Task<List<FleetActionRun>> EnumerateAsync(string tenantId, string userId, int limit = 100, CancellationToken token = default);
        /// <summary>List one ordered page of active runs across owners for trusted worker recovery and ticks.</summary>
        Task<List<FleetActionRun>> EnumerateActiveAsync(int limit = 100, string? afterId = null, CancellationToken token = default);
        /// <summary>Replace mutable state only when the exact-owner revision still matches.</summary>
        Task<FleetActionRun?> TryCompareExchangeStateAsync(string tenantId, string userId, string runId, long expectedRevision, FleetActionRunState nextState, CancellationToken token = default);
    }
}
