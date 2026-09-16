namespace Armada.Core.Services
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Stores a durable-lesson proposal (D18 <c>memory_candidate</c>) where an operator can review it.
    /// The proposal is never memory itself: an implementation writes it to the proposal store, never to
    /// the AI-Memory folder. Writing must never throw into the caller.
    /// </summary>
    public interface IMemoryCandidateProposalWriter
    {
        /// <summary>
        /// Store one proposal. Returns the stored proposal identifier (an existing one when the same
        /// subject was already proposed), or null when the write failed. Never throws.
        /// </summary>
        /// <param name="proposal">The proposal to store. Its text fields are already redacted.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The proposal identifier, or null.</returns>
        Task<string?> WriteAsync(MemoryCandidateProposal proposal, CancellationToken token);
    }
}
