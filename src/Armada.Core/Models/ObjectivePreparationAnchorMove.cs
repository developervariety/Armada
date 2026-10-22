namespace Armada.Core.Models
{
    /// <summary>
    /// A prepared branch anchor whose branch moved past the recorded commit without touching any path the
    /// objective cites, claims, or is assigned. Dispatch uses the current tip in place of the recorded
    /// commit; the stored anchor keeps the commit the preparation was verified at.
    /// </summary>
    public class ObjectivePreparationAnchorMove
    {
        /// <summary>Which anchor moved: <c>source</c> or <c>target</c>.</summary>
        public string Anchor { get; set; } = String.Empty;

        /// <summary>Branch the anchor names.</summary>
        public string Ref { get; set; } = String.Empty;

        /// <summary>Commit the preparation was verified at.</summary>
        public string RecordedCommit { get; set; } = String.Empty;

        /// <summary>Commit the branch resolves to now; dispatch uses this one.</summary>
        public string CurrentCommit { get; set; } = String.Empty;

        /// <summary>Number of paths that changed between the two commits.</summary>
        public int ChangedFileCount { get; set; }
    }
}
