namespace Armada.Core.Models
{
    /// <summary>Recorded delivery that still needs objective acceptance review.</summary>
    public sealed class CampaignCloseoutDelivery
    {
        /// <summary>The unfinished objective that links the delivered mission.</summary>
        public string ObjectiveId { get; set; } = String.Empty;
        /// <summary>The merge entry that retains the proof.</summary>
        public string MergeEntryId { get; set; } = String.Empty;
        /// <summary>The recorded commit/trailer proof; it does not establish criterion completion.</summary>
        public LandingReconcileEvidence Evidence { get; set; } = new LandingReconcileEvidence();
    }
}
