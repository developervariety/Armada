namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>An explicit owner decision to retire cancelled campaign scope without claiming delivery.</summary>
    public sealed class CampaignCloseoutRetirement
    {
        /// <summary>Cancelled descendant whose scope was retired.</summary>
        public string ObjectiveId { get; set; } = String.Empty;
        /// <summary>Verified owner-decision claim on the campaign root.</summary>
        public string ClaimId { get; set; } = String.Empty;
        /// <summary>Recorded reason for retirement.</summary>
        public string Reason { get; set; } = String.Empty;
        /// <summary>Evidence supporting the owner's decision.</summary>
        public List<string> EvidenceLinks { get; set; } = new List<string>();
        /// <summary>Time when the decision was verified.</summary>
        public DateTime VerifiedUtc { get; set; }
    }
}
