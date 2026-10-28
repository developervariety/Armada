namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Advisory repository health for one vessel. <see cref="Advisory"/> is always true:
    /// the report does not block dispatch, replace readiness, preflight, exact-commit Checks,
    /// or the definition-of-done gate, and the evaluation does not change the repository.
    /// </summary>
    public sealed class RepositoryHealthReport
    {
        /// <summary>Vessel the report describes.</summary>
        public string VesselId { get; set; } = String.Empty;

        /// <summary>True. Repository health is advisory and never a gate.</summary>
        public bool Advisory { get; set; } = true;

        /// <summary>Whether the owner host produced a report.</summary>
        public RepositoryHealthAvailabilityEnum Availability { get; set; } = RepositoryHealthAvailabilityEnum.Available;

        /// <summary>Stable reason when <see cref="Availability"/> is unavailable.</summary>
        public string? UnavailableReason { get; set; }

        /// <summary>UTC time the report was computed.</summary>
        public DateTime EvaluatedUtc { get; set; }

        /// <summary>Findings. An empty list means every evaluated criterion passed.</summary>
        public List<RepositoryHealthFinding> Findings { get; set; } = new List<RepositoryHealthFinding>();
    }
}
