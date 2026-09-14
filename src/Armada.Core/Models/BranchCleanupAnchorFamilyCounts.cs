namespace Armada.Core.Models
{
    /// <summary>
    /// Counts for one reclaim anchor ref family (refs/armada/docks/ or refs/armada/missions/) in a
    /// branch-cleanup sweep. Counts are per ref: an anchor present in the vessel bare and on origin
    /// is counted once on each side. Every candidate ends in exactly one kept, removed or failed count.
    /// </summary>
    public class BranchCleanupAnchorFamilyCounts
    {
        #region Public-Members

        /// <summary>
        /// Anchors of this family found in the vessel bare and on origin.
        /// </summary>
        public int Candidates { get; set; } = 0;

        /// <summary>
        /// Anchors kept because the dock or mission they name is still live.
        /// </summary>
        public int KeptActive { get; set; } = 0;

        /// <summary>
        /// Anchors kept because a recover/ branch points at the same commit.
        /// </summary>
        public int KeptRecoverPointer { get; set; } = 0;

        /// <summary>
        /// Anchors kept because their tip is not an ancestor of the default branch in the vessel bare.
        /// </summary>
        public int KeptUnlanded { get; set; } = 0;

        /// <summary>
        /// Landed anchors kept because they are inside the retention window, retention is disabled,
        /// or their commit time could not be read.
        /// </summary>
        public int KeptInRetention { get; set; } = 0;

        /// <summary>
        /// Anchors deleted from the vessel bare.
        /// </summary>
        public int SweptLocal { get; set; } = 0;

        /// <summary>
        /// Anchors deleted from origin (LocalAndRemote policy).
        /// </summary>
        public int SweptRemote { get; set; } = 0;

        #endregion
    }
}
