namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>Versioned, replayable cleanup intent for one Harbor-owned checkout.</summary>
    public sealed class HarborCheckoutLifecycleIntent
    {
        /// <summary>Storage schema version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Stable ID used in every remote operation key for this cleanup.</summary>
        public string IntentId { get; set; } = String.Empty;

        /// <summary>Exact persisted checkout binding revision this lifecycle may mutate.</summary>
        public string BindingRevision { get; set; } = String.Empty;

        /// <summary>Dock whose cleanup this intent owns.</summary>
        public string DockId { get; set; } = String.Empty;

        /// <summary>Mission associated with recovery output, when present.</summary>
        public string? MissionId { get; set; }

        /// <summary>Branch name whose policy is captured by this intent.</summary>
        public string? BranchName { get; set; }

        /// <summary>Workflow that first created this immutable intent.</summary>
        public HarborCheckoutLifecyclePurposeEnum Purpose { get; set; }

        /// <summary>Full checkout HEAD pinned before the first remote mutation.</summary>
        public string InitialHead { get; set; } = String.Empty;

        /// <summary>Current full HEAD expected by the next replayable mutation phase.</summary>
        public string ExpectedHead { get; set; } = String.Empty;

        /// <summary>Commit created by a work snapshot, when one was needed.</summary>
        public string? SnapshotCommitSha { get; set; }

        /// <summary>Recovery ref created by a work snapshot, when one was needed.</summary>
        public string? SnapshotRefName { get; set; }

        /// <summary>Head captured by the dock and mission recovery references.</summary>
        public string? AnchoredHead { get; set; }

        /// <summary>Fixed dock recovery ref written for the bound checkout.</summary>
        public string? DockAnchorRef { get; set; }

        /// <summary>Fixed mission recovery ref written when a mission owns the dock.</summary>
        public string? MissionAnchorRef { get; set; }

        /// <summary>Fixed branch recovery ref written when the dock has a branch.</summary>
        public string? BranchAnchorRef { get; set; }

        /// <summary>Whether to save uncommitted work before checkout removal.</summary>
        public bool PreserveSnapshot { get; set; }

        /// <summary>Whether to anchor dock and mission refs before checkout removal.</summary>
        public bool PreserveReferences { get; set; }

        /// <summary>Whether to preserve the captain branch before checkout removal.</summary>
        public bool PreserveBranch { get; set; }

        /// <summary>Whether the configured local branch is removed during cleanup.</summary>
        public bool DeleteLocalBranch { get; set; }

        /// <summary>Whether the configured remote branch is removed during cleanup.</summary>
        public bool DeleteRemoteBranch { get; set; }

        /// <summary>Highest remote operation phase with an accepted replay receipt.</summary>
        public HarborCheckoutLifecyclePhaseEnum Phase { get; set; } = HarborCheckoutLifecyclePhaseEnum.Prepared;

        /// <summary>Whether the runner accepted removal of the exact bound checkout.</summary>
        public bool RemovalConfirmed { get; set; }

        /// <summary>Whether cleanup continues from the retained repository after a prior checkout removal.</summary>
        public bool CheckoutAlreadyRemoved { get; set; }

        internal string? StoredJson { get; set; }
    }
}
