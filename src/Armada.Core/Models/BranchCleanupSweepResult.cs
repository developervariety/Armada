namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Result of one branch-cleanup maintenance sweep. Counts are per ref: a branch present in the
    /// vessel bare and on origin is counted once on each side.
    /// </summary>
    public class BranchCleanupSweepResult
    {
        #region Public-Members

        /// <summary>
        /// UTC timestamp of the sweep.
        /// </summary>
        public DateTime ScannedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Vessels the sweep examined.
        /// </summary>
        public int SweptVessels { get; set; } = 0;

        /// <summary>
        /// Vessels skipped because their cleanup policy is None or they lack a local repository.
        /// </summary>
        public int SkippedVessels { get; set; } = 0;

        /// <summary>
        /// Vessels, or a vessel's origin half, that could not be swept because of an error.
        /// </summary>
        public int VesselErrors { get; set; } = 0;

        /// <summary>
        /// Armada-owned branches found in vessel bare repositories.
        /// </summary>
        public int LocalCandidates { get; set; } = 0;

        /// <summary>
        /// Armada-owned branches found on origin.
        /// </summary>
        public int RemoteCandidates { get; set; } = 0;

        /// <summary>
        /// Candidate branches whose tip is an ancestor of the default branch and no active mission owns.
        /// </summary>
        public int Merged { get; set; } = 0;

        /// <summary>
        /// Candidate branches kept because their tip is not an ancestor of the default branch.
        /// </summary>
        public int KeptUnmerged { get; set; } = 0;

        /// <summary>
        /// Branches and preserved refs kept because a non-terminal mission names the branch.
        /// </summary>
        public int KeptActive { get; set; } = 0;

        /// <summary>
        /// Branches deleted from the local bare repository.
        /// </summary>
        public int SweptLocal { get; set; } = 0;

        /// <summary>
        /// Branches deleted from origin (LocalAndRemote policy).
        /// </summary>
        public int SweptRemote { get; set; } = 0;

        /// <summary>
        /// Preserved refs found in vessel bare repositories and on origin.
        /// </summary>
        public int PreservedCandidates { get; set; } = 0;

        /// <summary>
        /// Preserved refs kept because their tip is not an ancestor of the default branch.
        /// </summary>
        public int KeptPreservedUnlanded { get; set; } = 0;

        /// <summary>
        /// Landed preserved refs kept because they are inside the retention window, retention is
        /// disabled, or their commit time could not be read.
        /// </summary>
        public int KeptPreservedInRetention { get; set; } = 0;

        /// <summary>
        /// Preserved refs deleted from the local bare repository.
        /// </summary>
        public int SweptPreservedLocal { get; set; } = 0;

        /// <summary>
        /// Preserved refs deleted from origin (LocalAndRemote policy).
        /// </summary>
        public int SweptPreservedRemote { get; set; } = 0;

        /// <summary>
        /// Dock anchors (refs/armada/docks/&lt;dockId&gt;) written when a dock is reclaimed.
        /// </summary>
        public BranchCleanupAnchorFamilyCounts DockAnchors { get; set; } = new BranchCleanupAnchorFamilyCounts();

        /// <summary>
        /// Mission anchors (refs/armada/missions/&lt;missionId&gt;) written when a dock is reclaimed.
        /// </summary>
        public BranchCleanupAnchorFamilyCounts MissionAnchors { get; set; } = new BranchCleanupAnchorFamilyCounts();

        /// <summary>
        /// Origin deletions that found the ref already gone. Counted apart from removals and failures:
        /// the ref is in the desired state, but this run did not remove it.
        /// </summary>
        public int RemoteAlreadyAbsent { get; set; } = 0;

        /// <summary>
        /// Deletions or ancestry checks that failed; each is logged.
        /// </summary>
        public int Failed { get; set; } = 0;

        /// <summary>
        /// One entry per skipped vessel or skipped origin half, as "vesselId: reason".
        /// </summary>
        public List<string> SkipReasons { get; set; } = new List<string>();

        /// <summary>
        /// True when cancellation stopped the sweep before every vessel was examined.
        /// </summary>
        public bool Cancelled { get; set; } = false;

        /// <summary>
        /// The summary line the sweep logged for this run.
        /// </summary>
        public string Summary { get; set; } = String.Empty;

        #endregion
    }
}
