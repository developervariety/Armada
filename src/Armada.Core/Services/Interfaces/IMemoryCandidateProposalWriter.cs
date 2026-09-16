namespace Armada.Core.Services
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Writes a durable-lesson proposal (D18 <c>memory_candidate</c>) somewhere the owner can review
    /// it. The proposal is never memory itself: an implementation writes it under the proposals
    /// folder, never under <c>shared/</c> or <c>repos/</c>. Writing must never throw into the caller.
    /// </summary>
    public interface IMemoryCandidateProposalWriter
    {
        /// <summary>
        /// Write one proposal. Returns the path written, or null when no writable proposals folder is
        /// configured or the write failed. Never throws.
        /// </summary>
        /// <param name="proposal">The proposal to write. Its text fields are already redacted.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The written path, or null.</returns>
        Task<string?> WriteAsync(MemoryCandidateProposal proposal, CancellationToken token);
    }
}
