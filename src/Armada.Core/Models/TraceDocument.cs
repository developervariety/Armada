namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// The compact, payload-free description of one objective, voyage, mission, check run, incident,
    /// merge entry or deployment that a trace record keeps after the bulky source data is archived or
    /// expired. It holds identifiers, links, the status timeline, commits, gate and check totals, the
    /// Judge verdict, and pointers to archived raw data. It never holds secrets: free text is redacted
    /// and bounded before it is stored.
    /// </summary>
    public sealed class TraceDocument
    {
        #region Public-Members

        /// <summary>Maximum stored length of the final message.</summary>
        public const int MaximumFinalMessageLength = 4096;

        /// <summary>Maximum stored length of a failure reason or summary.</summary>
        public const int MaximumReasonLength = 1000;

        /// <summary>Maximum stored length of a title.</summary>
        public const int MaximumTitleLength = 200;

        /// <summary>Maximum number of timeline entries kept, newest last.</summary>
        public const int MaximumTimelineEntries = 60;

        /// <summary>Maximum number of failing test names kept.</summary>
        public const int MaximumFailedTests = 20;

        /// <summary>Record id; the id of the described record.</summary>
        public string Id { get; set; } = String.Empty;

        /// <summary>Kind of the described record.</summary>
        public TraceEntityKindEnum Kind { get; set; } = TraceEntityKindEnum.Mission;

        /// <summary>Title, bounded.</summary>
        public string? Title { get; set; } = null;

        /// <summary>Status at the time of the last write.</summary>
        public string? Status { get; set; } = null;

        /// <summary>Tenant of the described record.</summary>
        public string? TenantId { get; set; } = null;

        /// <summary>When the described record was created.</summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>When work on the described record started.</summary>
        public DateTime? StartedUtc { get; set; } = null;

        /// <summary>When the described record reached its end state.</summary>
        public DateTime? CompletedUtc { get; set; } = null;

        /// <summary>When the described record last changed.</summary>
        public DateTime LastUpdateUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Vessel the work ran on.</summary>
        public string? VesselId { get; set; } = null;

        /// <summary>Captain that ran the work.</summary>
        public string? CaptainId { get; set; } = null;

        /// <summary>Model of the captain that ran the work.</summary>
        public string? Model { get; set; } = null;

        /// <summary>Persona of a mission.</summary>
        public string? Persona { get; set; } = null;

        /// <summary>Pipeline stage position of a mission.</summary>
        public int? StageOrder { get; set; } = null;

        /// <summary>Branch the work was made on.</summary>
        public string? BranchName { get; set; } = null;

        /// <summary>Ref the work started from.</summary>
        public string? StartRef { get; set; } = null;

        /// <summary>Commit the record names: a mission's work commit, a check's tested commit, a deployment's source commit.</summary>
        public string? CommitSha { get; set; } = null;

        /// <summary>Commit that reached the target branch: the mission commit of a landed merge entry, or of a mission whose merge entry landed.</summary>
        public string? LandedSha { get; set; } = null;

        /// <summary>Artifact a deployment shipped, such as an image digest.</summary>
        public string? ArtifactIdentity { get; set; } = null;

        /// <summary>Latest definition-of-done gate result of a mission.</summary>
        public TraceGateResult? Gate { get; set; } = null;

        /// <summary>Result of a check run.</summary>
        public TraceCheckResult? Check { get; set; } = null;

        /// <summary>Judge verdict label of a Judge mission (PASS, NEEDS_REVISION or FAIL).</summary>
        public string? Verdict { get; set; } = null;

        /// <summary>Number of blocking findings in a Judge mission's review.</summary>
        public int? FindingCount { get; set; } = null;

        /// <summary>Failure reason, redacted and bounded.</summary>
        public string? FailureReason { get; set; } = null;

        /// <summary>Tail of the final captain message, redacted and bounded.</summary>
        public string? FinalMessage { get; set; } = null;

        /// <summary>Links to the related records.</summary>
        public List<TraceLink> Links { get; set; } = new List<TraceLink>();

        /// <summary>Lifecycle events of the described record, oldest first.</summary>
        public List<TraceTimelineEntry> Timeline { get; set; } = new List<TraceTimelineEntry>();

        /// <summary>Archived raw data that belongs to the described record.</summary>
        public List<TraceArchivePointer> Archive { get; set; } = new List<TraceArchivePointer>();

        #endregion
    }
}
