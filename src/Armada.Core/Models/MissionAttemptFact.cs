namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// One append-only attempt fact for a mission. The row carries the attempt chain identity
    /// (the original mission the chain started from) and the typed autonomous-rescue marker, so
    /// first-pass acceptance and rescue cost are computed from stored facts.
    /// </summary>
    public sealed class MissionAttemptFact
    {
        /// <summary>Maximum stored length of a reason code.</summary>
        public const int MaximumReasonCodeLength = 96;

        /// <summary>Unique identifier.</summary>
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable(Constants.MissionAttemptFactIdPrefix, 24);

        /// <summary>Tenant identifier.</summary>
        public string? TenantId { get; set; } = null;

        /// <summary>User identifier.</summary>
        public string? UserId { get; set; } = null;

        /// <summary>Mission the fact describes.</summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>Voyage of the mission when the fact was recorded.</summary>
        public string? VoyageId { get; set; } = null;

        /// <summary>Vessel of the mission when the fact was recorded.</summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Original mission of the attempt chain. A mission that is not recovery work is its own root.
        /// </summary>
        public string RootMissionId { get; set; } = String.Empty;

        /// <summary>Mission this recovery work derives from, when any.</summary>
        public string? ParentMissionId { get; set; } = null;

        /// <summary>Fact type.</summary>
        public MissionAttemptFactTypeEnum FactType { get; set; } = MissionAttemptFactTypeEnum.AttemptStarted;

        /// <summary>True when the mission carries the autonomous-rescue marker.</summary>
        public bool IsRescue { get; set; } = false;

        /// <summary>
        /// Bounded machine reason code. Free text such as failure messages is never stored here.
        /// </summary>
        public string? ReasonCode { get; set; } = null;

        /// <summary>UTC time the fact was recorded.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }
}
