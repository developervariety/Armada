namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Enums;

    /// <summary>Versioned Git evidence tied to one provisioned dock and mission.</summary>
    public sealed class DockGitAnchorSnapshot
    {
        /// <summary>Snapshot schema version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Dock for which the evidence was captured.</summary>
        public string DockId { get; set; } = String.Empty;

        /// <summary>Mission for which the dock was provisioned.</summary>
        public string MissionId { get; set; } = String.Empty;

        /// <summary>Vessel whose repository supplied the commit.</summary>
        public string VesselId { get; set; } = String.Empty;

        /// <summary>Full commit object ID observed immediately after worktree creation.</summary>
        public string ProvisionedCommit { get; set; } = String.Empty;

        /// <summary>Time the provisioning commit was observed.</summary>
        public DateTime ProvisionedUtc { get; set; }

        /// <summary>Time the separate target tip and selected anchor queries were observed.</summary>
        public DateTime? ResolvedUtc { get; set; }

        /// <summary>Whether enrichment is pending, complete or incomplete.</summary>
        public DockGitAnchorStateEnum State { get; set; } = DockGitAnchorStateEnum.Seeded;

        /// <summary>Bounded facts from the provisioning commit and the separately observed target tip.</summary>
        public GitAnchors Anchors { get; set; } = new GitAnchors();

        /// <summary>True when selected snapshot data had to be omitted to retain storage bounds.</summary>
        public bool Truncated { get; set; }

        /// <summary>Sanitized failure code; never raw command output or an exception message.</summary>
        public string? ErrorCode { get; set; }

        internal string? StoredJson { get; set; }
    }
}
