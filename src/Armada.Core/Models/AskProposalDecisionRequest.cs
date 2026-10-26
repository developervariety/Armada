namespace Armada.Core.Models
{
    using System;

    /// <summary>The immutable proposal fingerprint the human reviewed.</summary>
    public sealed class AskProposalDecisionRequest
    {
        /// <summary>Fingerprint shown beside the exact tool and arguments.</summary>
        public string Fingerprint { get; set; } = String.Empty;
    }
}
