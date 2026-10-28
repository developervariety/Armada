namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>Safe metadata for one preview or committed full-chain Research replay.</summary>
    public sealed class ResearchReplayPlan
    {
        /// <summary>Existing voyage whose stage identities are retained.</summary>
        public string VoyageId { get; set; } = String.Empty;

        /// <summary>Exact immutable source commit for all six stages.</summary>
        public string SourceCommit { get; set; } = String.Empty;

        /// <summary>Ordered original mission identities.</summary>
        public List<string> MissionIds { get; set; } = new List<string>();

        /// <summary>Historical terminal status and chronology of all six archived duties.</summary>
        public List<ResearchReplayStage> PriorStages { get; set; } = new List<ResearchReplayStage>();

        /// <summary>Known limits of historical capture, also retained in the immutable receipt.</summary>
        public List<string> ArchiveLimitations { get; set; } = new List<string>();

        /// <summary>Digest of the complete versioned expected state, including retained reports.</summary>
        public string Fingerprint { get; set; } = String.Empty;

        /// <summary>Preview-time gate configuration digest; not a reconstruction of original launch settings.</summary>
        public string? ConfigurationDigest { get; set; }

        /// <summary>Durable replay identity, present only after the transaction commits.</summary>
        public string? ReplayId { get; set; }

        /// <summary>True only for a committed transaction or its idempotent result.</summary>
        public bool Applied { get; set; }

        /// <summary>True only in the process that just committed; never part of the persisted or public receipt.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool AppliedNow { get; set; }

        /// <summary>UTC time at which the prior attempt was durably archived.</summary>
        public DateTime? ArchivedUtc { get; set; }
    }
}
