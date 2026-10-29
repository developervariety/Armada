namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The attempts recorded for one mission, oldest first.
    /// </summary>
    public sealed class MissionAttemptProvenanceReport
    {
        /// <summary>Mission the attempts belong to.</summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>Attempts, oldest first. Holds the most recent attempts when the mission has more than the limit.</summary>
        public List<MissionAttemptProvenance> Attempts { get; set; } = new List<MissionAttemptProvenance>();

        /// <summary>True when older attempts exist beyond the returned page.</summary>
        public bool Truncated { get; set; } = false;
    }
}
