namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Result of a disk-lifecycle scan or reconciliation pass.
    /// </summary>
    public class DiskLifecycleReport
    {
        #region Public-Members

        /// <summary>
        /// UTC timestamp of the scan.
        /// </summary>
        public DateTime ScannedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Whether destructive reclamation is enabled in settings.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Whether this pass ran in dry-run mode (nothing deleted).
        /// </summary>
        public bool DryRun { get; set; } = true;

        /// <summary>
        /// Total bytes scanned across categories.
        /// </summary>
        public long TotalBytes { get; set; } = 0;

        /// <summary>
        /// Total bytes eligible for reclamation.
        /// </summary>
        public long TotalReclaimableBytes { get; set; } = 0;

        /// <summary>
        /// Total items eligible for reclamation.
        /// </summary>
        public int ReclaimableItems { get; set; } = 0;

        /// <summary>
        /// Total items skipped with a recorded reason (fail-closed).
        /// </summary>
        public int SkippedItems { get; set; } = 0;

        /// <summary>
        /// Total items protected because they are active or referenced.
        /// </summary>
        public int ProtectedItems { get; set; } = 0;

        /// <summary>
        /// Per-category byte accounting.
        /// </summary>
        public List<DiskLifecycleCategory> Categories { get; set; } = new List<DiskLifecycleCategory>();

        /// <summary>
        /// Per-item disposition records. Capped to keep the payload bounded.
        /// </summary>
        public List<DiskLifecycleAction> Actions { get; set; } = new List<DiskLifecycleAction>();

        /// <summary>
        /// Free bytes on the log volume when the pass started; -1 when it could not be read.
        /// </summary>
        public long FreeBytes { get; set; } = -1;

        /// <summary>
        /// True when free space was below the low-space threshold, so the pass used the shorter
        /// low-space retention for operator-evidence payload.
        /// </summary>
        public bool LowSpace { get; set; } = false;

        /// <summary>
        /// Number of failures in this pass, such as a folder that could not be listed. A category whose
        /// folder could not be read reports zero bytes, so a non-zero count means the totals are incomplete.
        /// </summary>
        public int ErrorCount { get; set; } = 0;

        /// <summary>
        /// Failure messages naming the path and the error. Capped to keep the payload bounded;
        /// <see cref="ErrorCount"/> carries the full count.
        /// </summary>
        public List<string> Errors { get; set; } = new List<string>();

        /// <summary>
        /// How the pass treated history: <c>Archive</c> (uploaded and verified before it is freed),
        /// <c>Keep</c> (kept because no archive is configured) or <c>Delete</c> (freed without a copy).
        /// </summary>
        public string HistoryMode { get; set; } = "Keep";

        /// <summary>
        /// History files this pass verified in the archive, uploaded now or already stored.
        /// </summary>
        public int ArchivedItems { get; set; } = 0;

        /// <summary>
        /// Files counted in <see cref="ArchivedItems"/> whose verified object already existed, so this
        /// pass sent no upload for them.
        /// </summary>
        public int ArchiveReusedItems { get; set; } = 0;

        /// <summary>
        /// Bytes of history this pass verified in the archive.
        /// </summary>
        public long ArchivedBytes { get; set; } = 0;

        /// <summary>
        /// History uploads that failed verification. Each failed file is kept.
        /// </summary>
        public int ArchiveFailures { get; set; } = 0;

        /// <summary>
        /// History items whose files are all verified in the archive but whose local copy could not be
        /// removed. Each is named in <see cref="Errors"/>, and the next pass retries the removal.
        /// </summary>
        public int ArchiveFreeFailures { get; set; } = 0;

        /// <summary>
        /// True when the pass reached <c>diskLifecycle.archiveMaxFilesPerPass</c> and deferred expired
        /// history to the next pass.
        /// </summary>
        public bool ArchiveCapped { get; set; } = false;

        /// <summary>
        /// Expired history items the pass deferred to the next pass because it reached the file bound.
        /// Each is counted reclaimable and recorded with the disposition <c>deferred</c>.
        /// </summary>
        public int ArchiveDeferredItems { get; set; } = 0;

        /// <summary>
        /// Object key of the manifest this pass uploaded, or null when it archived nothing.
        /// </summary>
        public string? ArchiveManifestKey { get; set; } = null;

        #endregion
    }
}
