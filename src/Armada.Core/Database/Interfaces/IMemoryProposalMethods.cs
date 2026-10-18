namespace Armada.Core.Database.Interfaces
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Durable memory proposals. Rows are created by the typed-decision writers and dismissed or marked
    /// promoted only by an operator; nothing deletes a proposal and nothing rewrites its text after
    /// creation. Nothing here writes AI-Memory.
    /// </summary>
    public interface IMemoryProposalMethods
    {
        /// <summary>Store one proposal.</summary>
        /// <param name="proposal">Proposal to store.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored proposal.</returns>
        Task<MemoryProposal> CreateAsync(MemoryProposal proposal, CancellationToken token = default);

        /// <summary>Read one proposal by identifier.</summary>
        /// <param name="id">Proposal identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The proposal, or null.</returns>
        Task<MemoryProposal?> ReadAsync(string id, CancellationToken token = default);

        /// <summary>Read the first proposal stored for a subject fingerprint, in any state.</summary>
        /// <param name="sourceKey">Subject fingerprint.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The proposal, or null.</returns>
        Task<MemoryProposal?> ReadBySourceKeyAsync(string sourceKey, CancellationToken token = default);

        /// <summary>List proposals, newest first.</summary>
        /// <param name="tenantId">Tenant filter, or null for every tenant.</param>
        /// <param name="state">State filter, or null for every state.</param>
        /// <param name="limit">Maximum rows, clamped to [1, 1000].</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The proposals.</returns>
        Task<List<MemoryProposal>> EnumerateAsync(string? tenantId, MemoryProposalStateEnum? state, int limit, CancellationToken token = default);

        /// <summary>
        /// Dismiss an open proposal. The write is conditional on the Open state, so a second dismissal
        /// changes nothing and returns false.
        /// </summary>
        /// <param name="id">Proposal identifier.</param>
        /// <param name="dismissedBy">Operator name.</param>
        /// <param name="reason">Dismissal reason.</param>
        /// <param name="dismissedUtc">Dismissal time in UTC.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when an open proposal was dismissed.</returns>
        Task<bool> DismissAsync(string id, string dismissedBy, string reason, DateTime dismissedUtc, CancellationToken token = default);

        /// <summary>
        /// Record that the owner wrote an open proposal into AI-Memory. The write is conditional on the
        /// Open state, so a dismissed or already promoted proposal changes nothing and returns false.
        /// </summary>
        /// <param name="id">Proposal identifier.</param>
        /// <param name="promotedBy">Operator name.</param>
        /// <param name="commitSha">The full 40-character AI-Memory commit SHA that holds the rule.</param>
        /// <param name="promotedUtc">Promotion time in UTC.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when an open proposal was marked promoted.</returns>
        Task<bool> PromoteAsync(string id, string promotedBy, string commitSha, DateTime promotedUtc, CancellationToken token = default);
    }
}
