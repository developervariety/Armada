namespace Armada.Core.Database.Interfaces
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>Owner-scoped durable persistence for proposed Ask actions.</summary>
    public interface IAskActionProposalMethods
    {
        /// <summary>Create a proposal only for the active running Ask turn, or return its matching duplicate.</summary>
        Task<AskActionProposal?> TryCreateAsync(AskActionProposal proposal, CancellationToken token = default);
        /// <summary>Read one proposal for its exact owner and thread.</summary>
        Task<AskActionProposal?> ReadAsync(string tenantId, string userId, string threadId, string proposalId, CancellationToken token = default);
        /// <summary>List proposals for one exact owner and thread.</summary>
        Task<List<AskActionProposal>> EnumerateAsync(string tenantId, string userId, string threadId, int limit = 100, CancellationToken token = default);
        /// <summary>Claim a pending, unexpired proposal by compare-and-set.</summary>
        Task<bool> TryClaimAsync(string tenantId, string userId, string threadId, string proposalId, string fingerprint, DateTime utcNow, CancellationToken token = default);
        /// <summary>Reject a pending proposal by compare-and-set.</summary>
        Task<bool> TryRejectAsync(string tenantId, string userId, string threadId, string proposalId, string fingerprint, DateTime utcNow, CancellationToken token = default);
        /// <summary>Record accepted executing work references without changing proposal state.</summary>
        Task<bool> RecordExecutingWorkAsync(string tenantId, string userId, string threadId, string proposalId, List<AskWorkReference> workReferences, CancellationToken token = default);
        /// <summary>Complete an executing proposal with a terminal result and typed work references.</summary>
        Task<bool> CompleteAsync(string tenantId, string userId, string threadId, string proposalId, AskProposalStateEnum state, string? outcome, List<AskWorkReference> workReferences, DateTime utcNow, CancellationToken token = default);
    }
}
