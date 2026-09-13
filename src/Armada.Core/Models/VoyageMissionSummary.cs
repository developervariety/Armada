namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>Scoped voyage mission counts and a page of associated vessel identifiers.</summary>
    public sealed class VoyageMissionSummary
    {
        /// <summary>Counts across all visible missions in the voyage, independent of the vessel page.</summary>
        public Dictionary<MissionStatusEnum, long> StatusCounts { get; set; } = new Dictionary<MissionStatusEnum, long>();

        /// <summary>Distinct visible vessel identifiers ordered by the database identifier collation.</summary>
        public EnumerationResult<string> Vessels { get; set; } = new EnumerationResult<string>();
    }
}
