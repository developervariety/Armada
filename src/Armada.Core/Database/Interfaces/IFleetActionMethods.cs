namespace Armada.Core.Database.Interfaces
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Exact-owner persistence for mission action definitions.</summary>
    public interface IFleetActionMethods
    {
        /// <summary>Create an action definition.</summary>
        Task<FleetAction> CreateAsync(FleetAction action, CancellationToken token = default);
        /// <summary>Read one action for its exact owner.</summary>
        Task<FleetAction?> ReadAsync(string tenantId, string userId, string actionId, CancellationToken token = default);
        /// <summary>List actions for one exact owner, newest updates first.</summary>
        Task<List<FleetAction>> EnumerateAsync(string tenantId, string userId, int limit = 100, CancellationToken token = default);
        /// <summary>Update one action for its exact owner.</summary>
        Task<bool> UpdateAsync(FleetAction action, CancellationToken token = default);
        /// <summary>Delete one action for its exact owner.</summary>
        Task<bool> DeleteAsync(string tenantId, string userId, string actionId, CancellationToken token = default);
    }
}
