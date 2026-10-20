namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// What one mission stage committed: every path that differs between the commit its dock was provisioned at
    /// and its recorded final commit, read from the vessel's repository with no dock. It is not a diff against the
    /// current target branch and holds no uncommitted files. <see cref="Available"/> false means nothing was
    /// compared and <see cref="Reason"/> names why; it never means "nothing changed". <see cref="Complete"/> false
    /// means only the first paths are listed.
    /// </summary>
    public sealed class MissionChangeSummary
    {
        #region Public-Members

        /// <summary>No mission id was supplied and the caller holds no mission credential.</summary>
        public const string InvalidReason = "invalid";

        /// <summary>No mission with that id is visible to the caller.</summary>
        public const string MissionNotFoundReason = "mission_not_found";

        /// <summary>The mission has no vessel.</summary>
        public const string NoVesselReason = "no_vessel";

        /// <summary>The vessel or its repository on this admiral cannot be read.</summary>
        public const string RepositoryUnavailableReason = "repository_unavailable";

        /// <summary>No recorded evidence names the commit the stage started from.</summary>
        public const string StartCommitNotRecordedReason = "start_commit_not_recorded";

        /// <summary>The mission has no recorded full final commit.</summary>
        public const string FinalCommitNotRecordedReason = "final_commit_not_recorded";

        /// <summary>No git service is configured.</summary>
        public const string GitUnavailableReason = "git_unavailable";

        /// <summary>Too many change-summary reads are running; retry later.</summary>
        public const string BusyReason = "busy";

        /// <summary>The calling mission has used its change-summary call budget.</summary>
        public const string BudgetReason = "budget";

        /// <summary>The read failed unexpectedly.</summary>
        public const string ExceptionReason = "exception";

        /// <summary>The mission the summary describes.</summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>The mission's vessel, when it was resolved.</summary>
        public string? VesselId { get; set; } = null;

        /// <summary>True when the two commits were compared.</summary>
        public bool Available { get; set; } = false;

        /// <summary>True when <see cref="Paths"/> holds every changed path.</summary>
        public bool Complete { get; set; } = false;

        /// <summary>
        /// Named reason: why the summary is unavailable, or why it is incomplete (<c>path_limit</c>). Null only when
        /// the summary is available and complete. The comparison codes are those on <see cref="ChangedPathStatusRead"/>.
        /// </summary>
        public string? Reason { get; set; } = null;

        /// <summary>Short explanation of <see cref="Reason"/>.</summary>
        public string? Message { get; set; } = null;

        /// <summary>Full id of the commit the stage started from, when recorded.</summary>
        public string? StartCommit { get; set; } = null;

        /// <summary>Which recorded evidence named <see cref="StartCommit"/>.</summary>
        public string? StartCommitSource { get; set; } = null;

        /// <summary>Full id of the stage's recorded final commit, when recorded.</summary>
        public string? FinalCommit { get; set; } = null;

        /// <summary>Changed paths in git's order. Empty when unavailable.</summary>
        public List<ChangedPathEntry> Paths { get; set; } = new List<ChangedPathEntry>();

        /// <summary>Number of paths listed in <see cref="Paths"/>.</summary>
        public int PathCount { get; set; } = 0;

        /// <summary>Number of paths that changed, when the comparison was read.</summary>
        public int? TotalPathCount { get; set; } = null;

        /// <summary>The path limit applied to this read.</summary>
        public int MaxPaths { get; set; } = 0;

        /// <summary>Calls this mission credential has made, when the caller holds one.</summary>
        public int? CallsUsed { get; set; } = null;

        /// <summary>Notes on how to read the answer.</summary>
        public List<string> Warnings { get; set; } = new List<string>();

        #endregion
    }
}
