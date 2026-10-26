namespace Armada.Core.Models
{
    /// <summary>Sanitized metadata for one existing working-tree checkout.</summary>
    public sealed class VesselCheckoutCandidate
    {
        /// <summary>Canonical path of the checkout.</summary>
        public string CheckoutPath { get; set; } = string.Empty;

        /// <summary>Suggested vessel name derived from the checkout directory.</summary>
        public string SuggestedName { get; set; } = string.Empty;

        /// <summary>Sanitized origin URL, when it can be returned without credential data.</summary>
        public string? OriginUrl { get; set; }

        /// <summary>Detected default branch, or main when Git has no symbolic default.</summary>
        public string DefaultBranch { get; set; } = "main";

        /// <summary>Existing vessel that already references this canonical path, if any.</summary>
        public string? ExistingVesselId { get; set; }
    }
}
