namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Models;

    /// <summary>
    /// Coordinates Admiral-owned code indexing, search, and context-pack generation.
    /// </summary>
    public interface ICodeIndexService
    {
        /// <summary>
        /// Get index status for a vessel.
        /// </summary>
        Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default);

        /// <summary>
        /// Refresh the index for a vessel.
        /// </summary>
        Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default);

        /// <summary>
        /// Search indexed vessel code.
        /// </summary>
        Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default);

        /// <summary>
        /// Search indexed code across all vessels in a fleet.
        /// </summary>
        Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default);

        /// <summary>
        /// Build a dispatch-ready context pack for a mission goal.
        /// </summary>
        Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default);

        /// <summary>
        /// Build a dispatch-ready context pack across all vessels in a fleet.
        /// </summary>
        Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default);

        /// <summary>
        /// Search symbols from vessel-scoped graph sidecars.
        /// </summary>
        Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default);

        /// <summary>
        /// Resolve direct callers for a symbol from vessel-scoped graph sidecars.
        /// </summary>
        Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default);

        /// <summary>
        /// Resolve direct callees for a symbol from vessel-scoped graph sidecars.
        /// </summary>
        Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default);

        /// <summary>
        /// Traverse graph relationships from a seed symbol using bounded depth and deterministic ordering.
        /// </summary>
        Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default);

        /// <summary>
        /// Suggest affected tests from graph traversal evidence and path conventions.
        /// </summary>
        Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default);
    }
}
