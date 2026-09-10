namespace Armada.Core.Models
{
    /// <summary>
    /// One effective operator mission start ref and its read-only resolution result.
    /// </summary>
    public class ObjectiveDispatchMissionRef
    {
        /// <summary>Zero-based position in the dispatch request.</summary>
        public int MissionIndex { get; set; }

        /// <summary>Mission title.</summary>
        public string Title { get; set; } = String.Empty;

        /// <summary>Effective mission mode.</summary>
        public string Mode { get; set; } = String.Empty;

        /// <summary>Effective first-stage start ref. Null means the vessel default branch.</summary>
        public string? StartFromRef { get; set; } = null;

        /// <summary>Commit resolved from the effective start ref.</summary>
        public string? ResolvedCommit { get; set; } = null;
    }
}
