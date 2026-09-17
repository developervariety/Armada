namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// One captain record change the tier record migration makes: the pinned tier that reproduces the retired
    /// tier lists, and the preference rank that reproduces the retired within-tier preference order.
    /// </summary>
    public sealed class CaptainTierMigrationChange
    {
        #region Public-Members

        /// <summary>Captain identifier.</summary>
        public string CaptainId { get; set; } = String.Empty;

        /// <summary>Captain name.</summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>Captain model, when set.</summary>
        public string? Model { get; set; } = null;

        /// <summary>The tier the retired lists and rules gave the model: <c>high</c>, <c>mid</c>, or null when none did.</summary>
        public string? RetiredTier { get; set; } = null;

        /// <summary>The captain's stored tier before the migration.</summary>
        public CaptainTierEnum? PreviousTier { get; set; } = null;

        /// <summary>The captain's stored tier after the migration.</summary>
        public CaptainTierEnum? Tier { get; set; } = null;

        /// <summary>The captain's preference rank before the migration.</summary>
        public int PreviousRank { get; set; } = 0;

        /// <summary>The captain's preference rank after the migration.</summary>
        public int Rank { get; set; } = 0;

        #endregion
    }
}
