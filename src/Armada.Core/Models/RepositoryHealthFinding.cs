namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// One advisory repository-health finding. The finding reports a criterion. It does not
    /// block dispatch or change the repository.
    /// </summary>
    public sealed class RepositoryHealthFinding
    {
        /// <summary>Criterion that produced the finding.</summary>
        public RepositoryHealthCriterionEnum Criterion { get; set; }

        /// <summary>How serious the finding is. It still does not block dispatch.</summary>
        public RepositoryHealthSeverityEnum Severity { get; set; }

        /// <summary>Stable code for the finding.</summary>
        public string Code { get; set; } = String.Empty;

        /// <summary>What the criterion observed.</summary>
        public string Message { get; set; } = String.Empty;
    }
}
