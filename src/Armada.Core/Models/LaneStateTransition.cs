namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>
    /// One append-only observation of a shared lane's dispatch eligibility, occupancy, and capacity.
    /// A row is written when the state changes and as a periodic checkpoint, so time after a row's
    /// trust window is reported as unobserved instead of being assumed.
    /// </summary>
    public sealed class LaneStateTransition
    {
        /// <summary>Maximum stored lane key length.</summary>
        public const int MaximumLaneKeyLength = 1024;

        /// <summary>Maximum stored eligible source-family list length.</summary>
        public const int MaximumSourceFamiliesLength = 512;

        /// <summary>Unique identifier.</summary>
        public string Id { get; set; } = Constants.IdGenerator.GenerateKSortable(Constants.LaneStateTransitionIdPrefix, 24);

        /// <summary>Sorted lane member vessel identifiers joined with '+'.</summary>
        public string LaneKey { get; set; } = String.Empty;

        /// <summary>Objectives eligible for dispatch into this lane.</summary>
        public int EligibleCount { get; set; }

        /// <summary>Active voyages occupying this lane.</summary>
        public int Occupied { get; set; }

        /// <summary>Lane capacity: the per-lane concurrent voyage limit.</summary>
        public int Capacity { get; set; }

        /// <summary>Fleet-wide block in force at the observation.</summary>
        public LaneBlockReasonEnum BlockReason { get; set; } = LaneBlockReasonEnum.None;

        /// <summary>Sorted distinct source families of the eligible objectives, comma separated.</summary>
        public string EligibleSourceFamilies { get; set; } = String.Empty;

        /// <summary>True when the state did not change and the row only proves continuity.</summary>
        public bool Checkpoint { get; set; }

        /// <summary>Seconds after <see cref="CreatedUtc"/> for which this observation is trusted.</summary>
        public int ValidForSeconds { get; set; }

        /// <summary>UTC observation time.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }
}
