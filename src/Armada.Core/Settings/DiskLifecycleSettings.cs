namespace Armada.Core.Settings
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Settings for the disk-lifecycle reconciler: bounded, observable reclamation of
    /// Armada-owned storage. Destructive periodic cleanup is OFF by default (dry-run
    /// observability only); the operator opts in by setting <see cref="Enabled"/> to true
    /// after reviewing dry-run reports. All reclamation fails closed: only paths under an
    /// allowed root that are not symlinks and are older than their grace period are eligible.
    /// </summary>
    public class DiskLifecycleSettings
    {
        #region Public-Members

        /// <summary>
        /// Whether periodic reconciliation may delete reclaimable items. When false, the
        /// reconciler only scans and reports (dry-run). Stale sibling leases are always
        /// reconciled because that is a metadata purge, not a destructive deletion.
        /// Defaults to false.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// When true, reconciliation never deletes anything and every eligible item is
        /// recorded with a dry-run disposition. Defaults to true so the first deployment
        /// is observability-only, per the rollout constraint.
        /// </summary>
        public bool DryRun { get; set; } = true;

        /// <summary>
        /// Number of health-check cycles between periodic reconciliations. Defaults to 50
        /// (~25 minutes at the default 30-second heartbeat).
        /// </summary>
        public int ReconcileIntervalCycles
        {
            get => _ReconcileIntervalCycles;
            set => _ReconcileIntervalCycles = Math.Max(5, Math.Min(1440, value));
        }

        /// <summary>
        /// Minimum age in minutes before an orphan dock directory is eligible for
        /// reclamation. Defaults to 30.
        /// </summary>
        public int OrphanDockGraceMinutes
        {
            get => _OrphanDockGraceMinutes;
            set => _OrphanDockGraceMinutes = Math.Max(1, Math.Min(10080, value));
        }

        /// <summary>
        /// Age in hours after which a stale sibling lease whose holder dock is no longer
        /// active is purged unconditionally. Defaults to 24.
        /// </summary>
        public int StaleLeaseGraceHours
        {
            get => _StaleLeaseGraceHours;
            set => _StaleLeaseGraceHours = Math.Max(1, Math.Min(720, value));
        }

        /// <summary>
        /// Retention in days for mission log files. Defaults to 30.
        /// </summary>
        public int MissionLogRetentionDays
        {
            get => _MissionLogRetentionDays;
            set => _MissionLogRetentionDays = Math.Max(0, Math.Min(3650, value));
        }

        /// <summary>
        /// Retention in days for mission diff snapshot files. Defaults to 30.
        /// </summary>
        public int DiffSnapshotRetentionDays
        {
            get => _DiffSnapshotRetentionDays;
            set => _DiffSnapshotRetentionDays = Math.Max(0, Math.Min(3650, value));
        }

        /// <summary>
        /// Retention in days for generated instruction-file snapshots. Defaults to 7.
        /// </summary>
        public int InstructionRetentionDays
        {
            get => _InstructionRetentionDays;
            set => _InstructionRetentionDays = Math.Max(0, Math.Min(3650, value));
        }

        /// <summary>
        /// Retention in days for dock start-commit metadata files. Defaults to 30.
        /// </summary>
        public int DockMetadataRetentionDays
        {
            get => _DockMetadataRetentionDays;
            set => _DockMetadataRetentionDays = Math.Max(0, Math.Min(3650, value));
        }

        /// <summary>
        /// Age in hours after which a leftover integration or merge-queue worktree is
        /// eligible for reclamation when no live queue entry references it. Defaults to 24.
        /// </summary>
        public int IntegrationWorktreeRetentionHours
        {
            get => _IntegrationWorktreeRetentionHours;
            set => _IntegrationWorktreeRetentionHours = Math.Max(1, Math.Min(720, value));
        }

        /// <summary>
        /// Age in hours after which an Armada-prefixed temp artifact is eligible for
        /// reclamation. Defaults to 24.
        /// </summary>
        public int TempArtifactRetentionHours
        {
            get => _TempArtifactRetentionHours;
            set => _TempArtifactRetentionHours = Math.Max(1, Math.Min(720, value));
        }

        /// <summary>
        /// Retention in days for deployment backup archives. Defaults to 7.
        /// </summary>
        public int BackupRetentionDays
        {
            get => _BackupRetentionDays;
            set => _BackupRetentionDays = Math.Max(0, Math.Min(3650, value));
        }

        /// <summary>
        /// Minimum number of newest backup archives always retained regardless of age.
        /// Defaults to 2 (protects the current and rollback snapshots).
        /// </summary>
        public int MinBackupCount
        {
            get => _MinBackupCount;
            set => _MinBackupCount = Math.Max(1, Math.Min(100, value));
        }

        /// <summary>
        /// Additional filesystem roots the reconciler may reclaim from, on top of the
        /// built-in Armada-owned roots (log, docks, repos, backups, and the system temp
        /// directory for Armada-prefixed artifacts). Any path outside these roots is
        /// skipped with a recorded reason; the reconciler never fabricates roots.
        /// </summary>
        public List<string> AllowedRoots { get; set; } = new List<string>();

        #endregion

        #region Private-Members

        private int _ReconcileIntervalCycles = 50;
        private int _OrphanDockGraceMinutes = 30;
        private int _StaleLeaseGraceHours = 24;
        private int _MissionLogRetentionDays = 30;
        private int _DiffSnapshotRetentionDays = 30;
        private int _InstructionRetentionDays = 7;
        private int _DockMetadataRetentionDays = 30;
        private int _IntegrationWorktreeRetentionHours = 24;
        private int _TempArtifactRetentionHours = 24;
        private int _BackupRetentionDays = 7;
        private int _MinBackupCount = 2;

        #endregion
    }
}
