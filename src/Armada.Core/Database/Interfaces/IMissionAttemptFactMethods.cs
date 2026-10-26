namespace Armada.Core.Database.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>Append-only durable mission attempt facts.</summary>
    public interface IMissionAttemptFactMethods
    {
        /// <summary>Append one fact.</summary>
        /// <param name="fact">Fact to store.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored fact.</returns>
        Task<MissionAttemptFact> CreateAsync(MissionAttemptFact fact, CancellationToken token = default);

        /// <summary>Read one immutable fact by its stable identifier.</summary>
        /// <param name="id">Fact identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored fact, or null if absent.</returns>
        Task<MissionAttemptFact?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>Read facts created inside a bounded window.</summary>
        /// <param name="query">Window and scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Facts in ascending creation order and a truncation flag.</returns>
        Task<ProductionFactPage<MissionAttemptFact>> EnumerateAsync(ProductionFactQuery query, CancellationToken token = default);

        /// <summary>Read the newest facts for one exact tenant and mission.</summary>
        /// <param name="tenantId">Exact tenant identifier.</param>
        /// <param name="missionId">Exact mission identifier.</param>
        /// <param name="limit">Maximum rows to return, clamped to [1, 1000].</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The newest bounded set, in ascending creation time and identifier order, with a truncation flag.</returns>
        Task<ProductionFactPage<MissionAttemptFact>> EnumerateByMissionAsync(string tenantId, string missionId, int limit = 1000, CancellationToken token = default);
    }
}
