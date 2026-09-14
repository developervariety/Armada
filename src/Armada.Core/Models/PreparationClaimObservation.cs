namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// One append-only observation of a bounded preparation claim. The row stores the claim
    /// identity, its immutable anchors, and a one-way fingerprint of its statement and evidence.
    /// Claim text, evidence paths, and search queries are never stored.
    /// </summary>
    public sealed class PreparationClaimObservation
    {
        /// <summary>Maximum stored source-family length.</summary>
        public const int MaximumSourceFamilyLength = 64;

        /// <summary>Unique identifier.</summary>
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable(Constants.PreparationClaimObservationIdPrefix, 24);

        /// <summary>Tenant identifier.</summary>
        public string? TenantId { get; set; } = null;

        /// <summary>User identifier.</summary>
        public string? UserId { get; set; } = null;

        /// <summary>Objective that owns the claim.</summary>
        public string ObjectiveId { get; set; } = String.Empty;

        /// <summary>Preparation claim identifier.</summary>
        public string ClaimId { get; set; } = String.Empty;

        /// <summary>Claim kind.</summary>
        public ObjectivePreparationClaimKindEnum ClaimKind { get; set; } = ObjectivePreparationClaimKindEnum.SourcePath;

        /// <summary>Source family of the owning objective, or unknown.</summary>
        public string SourceFamily { get; set; } = "unknown";

        /// <summary>Voyage the claim was delivered to, for reuse observations.</summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>Immutable source commit the claim was verified against.</summary>
        public string? SourceCommit { get; set; } = null;

        /// <summary>Immutable target commit the claim was verified against.</summary>
        public string? TargetCommit { get; set; } = null;

        /// <summary>SHA-256 fingerprint of the claim kind, statement, and evidence.</summary>
        public string EvidenceFingerprint { get; set; } = String.Empty;

        /// <summary>Observation type.</summary>
        public PreparationClaimObservationEnum Observation { get; set; } = PreparationClaimObservationEnum.Established;

        /// <summary>UTC time of the observation.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }
}
