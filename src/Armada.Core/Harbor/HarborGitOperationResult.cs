namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>Typed results of fixed fork Git service operations.</summary>
    public sealed class HarborGitOperationResult
    {
        /// <summary>String result.</summary>
        public string? Text { get; set; }

        /// <summary>Boolean result, retaining unavailable separately.</summary>
        public bool? Flag { get; set; }

        /// <summary>Integer result, retaining unavailable separately.</summary>
        public int? Number { get; set; }

        /// <summary>Commit timestamp result.</summary>
        public DateTime? TimeUtc { get; set; }

        /// <summary>Path or line list result.</summary>
        public List<string> Paths { get; set; } = new List<string>();

        /// <summary>Repository HEAD inspection result.</summary>
        public RepositoryHeadInspection? RepositoryHead { get; set; }

        /// <summary>Changed path evidence.</summary>
        public ChangedPathsRead? ChangedPaths { get; set; }

        /// <summary>Changed path status evidence.</summary>
        public ChangedPathStatusRead? ChangedPathStatuses { get; set; }

        /// <summary>Pinned anchor path evidence.</summary>
        public TrackedPathMatch? AnchorPath { get; set; }

        /// <summary>Pinned tracked-content search evidence.</summary>
        public GitAnchorPriorArt? PriorArt { get; set; }

        /// <summary>Bounded commit evidence.</summary>
        public List<GitAnchorCommit> Commits { get; set; } = new List<GitAnchorCommit>();

        /// <summary>Branch inventory from the owning repository.</summary>
        public List<BranchInfo> Branches { get; set; } = new List<BranchInfo>();

        /// <summary>Exact ref-tip inventory from the owning repository.</summary>
        public List<GitRefTip> RefTips { get; set; } = new List<GitRefTip>();

        /// <summary>Preserved dock work result.</summary>
        public DockWorkSnapshot? Snapshot { get; set; }

    }
}
