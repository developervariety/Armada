namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>Effective inputs to the Legacy Routing order for one captain.</summary>
    public sealed class CaptainRankingFactors
    {
        #region Public-Members

        /// <summary>Effective captain tier; the mission determines the tier order.</summary>
        public CaptainTierEnum Tier { get; set; }
        /// <summary>Profile dimension selected by the mission hint, or null when inactive.</summary>
        public string? CapabilityDimension { get; set; }
        /// <summary>Active dimension score; -1 means no profile, null means no active dimension.</summary>
        public long? CapabilityScore { get; set; }
        /// <summary>Within-tier preference rank; higher wins after capability.</summary>
        public int PreferenceRank { get; set; }
        /// <summary>Whether the configured external-provider preference applies.</summary>
        public bool ExternalProviderPreferred { get; set; }
        /// <summary>Whether the captain prefers the mission persona.</summary>
        public bool PersonaPreferred { get; set; }

        #endregion
    }
}
