namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Request payload for importing or refreshing an objective from GitHub.
    /// </summary>
    public class GitHubObjectiveImportRequest
    {
        /// <summary>
        /// Vessel whose GitHub repository should be queried.
        /// </summary>
        public string? VesselId { get; set; } = null;

        /// <summary>
        /// Existing objective identifier to refresh. When omitted, a new objective is created.
        /// </summary>
        public string? ObjectiveId { get; set; } = null;

        /// <summary>
        /// Source record type to import.
        /// </summary>
        public GitHubObjectiveSourceTypeEnum SourceType { get; set; } = GitHubObjectiveSourceTypeEnum.Issue;

        /// <summary>
        /// GitHub issue or pull-request number.
        /// </summary>
        public int Number { get; set; } = 0;

        /// <summary>
        /// Optional explicit objective-status override to apply after import.
        /// </summary>
        public ObjectiveStatusEnum? StatusOverride { get; set; } = null;
    }
}
