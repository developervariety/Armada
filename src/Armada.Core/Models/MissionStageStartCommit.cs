namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// The commit a mission's dock was provisioned at, and which recorded evidence named it.
    /// </summary>
    public sealed class MissionStageStartCommit
    {
        #region Public-Members

        /// <summary>The stage start record written for this mission when its dock was assigned named the commit.</summary>
        public const string StageStartRecordSource = "stage_start_record";

        /// <summary>The dock's stored Git anchor snapshot for this mission named the commit.</summary>
        public const string DockAnchorSnapshotSource = "dock_anchor_snapshot";

        /// <summary>The dock start record written at provisioning named the commit.</summary>
        public const string DockStartRecordSource = "dock_start_record";

        /// <summary>The mission's start ref is itself a full commit id.</summary>
        public const string StartRefCommitSource = "start_ref_commit";

        /// <summary>Full commit id.</summary>
        public string Commit
        {
            get { return _Commit; }
            set { _Commit = value ?? throw new ArgumentNullException(nameof(Commit)); }
        }

        /// <summary>Which evidence named the commit: one of the source constants on this type.</summary>
        public string Source
        {
            get { return _Source; }
            set { _Source = value ?? throw new ArgumentNullException(nameof(Source)); }
        }

        #endregion

        #region Private-Members

        private string _Commit = String.Empty;
        private string _Source = String.Empty;

        #endregion
    }
}
