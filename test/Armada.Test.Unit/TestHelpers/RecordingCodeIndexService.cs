namespace Armada.Test.Unit.TestHelpers
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Hand-rolled <see cref="ICodeIndexService"/> double that records
    /// <see cref="UpdateAsync"/> calls for merge-queue landing tests.
    /// </summary>
    public sealed class RecordingCodeIndexService : ICodeIndexService
    {
        private readonly object _Gate = new object();

        /// <summary>
        /// Vessel ids passed to <see cref="UpdateAsync"/>, in call order.
        /// </summary>
        public List<string> UpdateAsyncVesselIds { get; } = new List<string>();

        /// <summary>
        /// Returns true when at least one recorded update used the given vessel id.
        /// </summary>
        public bool HasUpdateForVessel(string vesselId)
        {
            lock (_Gate)
            {
                foreach (string id in UpdateAsyncVesselIds)
                {
                    if (id == vesselId) return true;
                }
                return false;
            }
        }

        /// <inheritdoc />
        public Task<CodeIndexStatus> GetStatusAsync(string vesselId, CancellationToken token = default)
        {
            return Task.FromResult(new CodeIndexStatus { VesselId = vesselId ?? "" });
        }

        /// <inheritdoc />
        public Task<CodeIndexStatus> UpdateAsync(string vesselId, CancellationToken token = default)
        {
            lock (_Gate)
            {
                UpdateAsyncVesselIds.Add(vesselId ?? "");
            }
            return Task.FromResult(new CodeIndexStatus { VesselId = vesselId ?? "" });
        }

        /// <inheritdoc />
        public Task<CodeSearchResponse> SearchAsync(CodeSearchRequest request, CancellationToken token = default)
        {
            return Task.FromResult(new CodeSearchResponse());
        }

        /// <inheritdoc />
        public Task<FleetCodeSearchResponse> SearchFleetAsync(FleetCodeSearchRequest request, CancellationToken token = default)
        {
            return Task.FromResult(new FleetCodeSearchResponse());
        }

        /// <inheritdoc />
        public Task<ContextPackResponse> BuildContextPackAsync(ContextPackRequest request, CancellationToken token = default)
        {
            return Task.FromResult(new ContextPackResponse());
        }

        /// <inheritdoc />
        public Task<FleetContextPackResponse> BuildFleetContextPackAsync(FleetContextPackRequest request, CancellationToken token = default)
        {
            return Task.FromResult(new FleetContextPackResponse());
        }

        /// <inheritdoc />
        public Task<CodeGraphSymbolSearchResponse> SearchSymbolsAsync(CodeGraphSymbolSearchRequest request, CancellationToken token = default)
        {
            return Task.FromResult(new CodeGraphSymbolSearchResponse());
        }

        /// <inheritdoc />
        public Task<CodeGraphNeighborsResponse> GetCallersAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
        {
            return Task.FromResult(new CodeGraphNeighborsResponse());
        }

        /// <inheritdoc />
        public Task<CodeGraphNeighborsResponse> GetCalleesAsync(CodeGraphNeighborsRequest request, CancellationToken token = default)
        {
            return Task.FromResult(new CodeGraphNeighborsResponse());
        }

        /// <inheritdoc />
        public Task<CodeGraphImpactResponse> GetImpactAsync(CodeGraphImpactRequest request, CancellationToken token = default)
        {
            return Task.FromResult(new CodeGraphImpactResponse());
        }

        /// <inheritdoc />
        public Task<CodeGraphAffectedTestsResponse> SuggestAffectedTestsAsync(CodeGraphAffectedTestsRequest request, CancellationToken token = default)
        {
            return Task.FromResult(new CodeGraphAffectedTestsResponse());
        }
    }
}
