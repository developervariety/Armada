namespace Armada.Core.Services.Interfaces
{
    using System;
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
        /// Build a fleet-wide staleness summary: every vessel whose persisted indexed commit differs
        /// from the current default-branch commit of its repository. Cheap: reads persisted metadata
        /// and one rev-parse per vessel; never clones a missing repository. Defaults to an empty
        /// summary so doubles and minimal implementations opt out unless they override it.
        /// </summary>
        Task<CodeIndexStalenessSummary> GetStalenessSummaryAsync(CancellationToken token = default)
        {
            return Task.FromResult(new CodeIndexStalenessSummary());
        }

        /// <summary>
        /// Detect HEAD changes that did not go through an Armada landing (direct pushes, manual
        /// merges, reconciliations) and schedule a debounced reindex for each stale vessel. Fetches
        /// the vessel's origin first so a direct push is observed. Returns the number of vessels
        /// scheduled for refresh. Defaults to no-op so doubles and minimal implementations opt out
        /// unless they override it.
        /// </summary>
        Task<int> SweepStalenessAsync(CancellationToken token = default)
        {
            return Task.FromResult(0);
        }

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
        /// Decide whether a vessel is large enough to prefer the search-only fast-pack path.
        /// Returns true when the vessel's indexed file count exceeds the configured fast-pack threshold.
        /// Cheap status lookup only; does not refresh or mutate the index. Defaults to false so that
        /// existing implementations opt out of the fast-pack path unless they override it.
        /// </summary>
        Task<bool> ShouldUseFastPackAsync(string vesselId, CancellationToken token = default)
        {
            return Task.FromResult(false);
        }

        /// <summary>
        /// Generate and persist a baseline context pack for the vessel, keyed by the current indexed commit SHA.
        /// Called in the background after each successful code-index refresh. Failures are non-fatal.
        /// </summary>
        Task WarmBaselineCacheAsync(string vesselId, CancellationToken token = default);

        /// <summary>
        /// Return the cached baseline context pack for the vessel if the cached entry matches the current
        /// indexed commit SHA; otherwise returns null (cache miss).
        /// </summary>
        Task<ContextPackResponse?> TryGetCachedContextPackAsync(ContextPackRequest request, CancellationToken token = default);

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

        /// <summary>
        /// Resolve one graph symbol with direct neighbors and optional source.
        /// </summary>
        Task<CodeGraphNodeResponse> GetNodeAsync(CodeGraphNodeRequest request, CancellationToken token = default)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Return indexed file/symbol structure for a vessel.
        /// </summary>
        Task<CodeGraphFileStructureResponse> GetFileStructureAsync(CodeGraphFileStructureRequest request, CancellationToken token = default)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Explore graph relationships and source sections around a query.
        /// </summary>
        Task<CodeGraphExploreResponse> ExploreAsync(CodeGraphExploreRequest request, CancellationToken token = default)
        {
            throw new NotSupportedException();
        }
    }
}
