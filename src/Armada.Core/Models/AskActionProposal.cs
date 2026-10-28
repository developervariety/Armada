namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>A durable, owner-scoped proposal to run one Ask action.</summary>
    public sealed class AskActionProposal
    {
        /// <summary>Proposal identifier.</summary>
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable(Constants.AskActionProposalIdPrefix, 24);
        /// <summary>Tenant identifier.</summary>
        public string TenantId { get; set; } = string.Empty;
        /// <summary>User identifier.</summary>
        public string UserId { get; set; } = string.Empty;
        /// <summary>Ask thread identifier.</summary>
        public string ThreadId { get; set; } = string.Empty;
        /// <summary>Request identifier of the Ask turn that created the proposal.</summary>
        public string RequestId { get; set; } = string.Empty;
        /// <summary>Requested tool name.</summary>
        public string ToolName { get; set; } = string.Empty;
        /// <summary>Canonical JSON arguments.</summary>
        public string ArgumentsJson { get; set; } = "{}";
        /// <summary>
        /// One-line human description of the action, with each target vessel shown by name where the proposal owner's
        /// tenant knows it and by id otherwise. Derived when the proposal is read; never stored, and never part of the
        /// fingerprint or the saved arguments.
        /// </summary>
        public string Summary { get; set; } = string.Empty;
        /// <summary>SHA-256 fingerprint of the canonical action payload.</summary>
        public string Fingerprint { get; set; } = string.Empty;
        /// <summary>Proposal state.</summary>
        public AskProposalStateEnum State { get; set; } = AskProposalStateEnum.Pending;
        /// <summary>Creation time in UTC.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Expiry time in UTC.</summary>
        public DateTime ExpiresUtc { get; set; }
        /// <summary>Decision time in UTC.</summary>
        public DateTime? DecidedUtc { get; set; }
        /// <summary>Completion time in UTC.</summary>
        public DateTime? CompletedUtc { get; set; }
        /// <summary>Bounded generic outcome summary.</summary>
        public string? Outcome { get; set; }
        /// <summary>Typed references to work created by the action.</summary>
        public List<AskWorkReference> WorkReferences { get; set; } = new List<AskWorkReference>();
    }
}
