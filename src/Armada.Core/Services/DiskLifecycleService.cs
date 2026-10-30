namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Archive;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Bounded, observable reclamation of Armada-owned disk storage. Every pass produces a
    /// per-category byte report and per-item disposition records; destructive deletion happens
    /// only when <see cref="DiskLifecycleSettings.Enabled"/> is true and
    /// <see cref="DiskLifecycleSettings.DryRun"/> is false. Reclamation fails closed: an item is
    /// only eligible when it sits under an allowed root, is not a symlink, is not referenced by
    /// active state, and is older than its grace period. Stale sibling leases are always
    /// reconciled because that is metadata, not user data.
    /// History categories (mission logs, diffs, instruction snapshots, dock metadata and stage records,
    /// final messages, whole operator-evidence folders and configured captain session roots) are never
    /// simply deleted: with the archive enabled each item is uploaded and verified before it is freed,
    /// and without it the item is kept unless <see cref="ArchiveSettings.KeepHistoryWithoutArchive"/>
    /// is turned off.
    /// </summary>
    public class DiskLifecycleService
    {
        #region Public-Members

        /// <summary>
        /// Builds the archive sink from the current archive settings at the start of each pass that
        /// archives. Defaults to the S3-compatible sink.
        /// </summary>
        public Func<ArchiveSettings, IArchiveSink> ArchiveSinkFactory
        {
            get => _ArchiveSinkFactory;
            set => _ArchiveSinkFactory = value ?? throw new ArgumentNullException(nameof(value));
        }

        #endregion

        #region Private-Members

        private string _Header = "[DiskLifecycleService] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private SiblingLeaseRegistry _Leases;
        private readonly SemaphoreSlim _PassGate = new SemaphoreSlim(1, 1);
        private long _LastProgressTicks = 0;
        private DiskLifecycleReport? _CurrentReport;
        private Func<ArchiveSettings, IArchiveSink> _ArchiveSinkFactory = settings => new S3ArchiveSink(settings);
        private HistoryRetentionModeEnum _HistoryMode = HistoryRetentionModeEnum.Keep;
        private HistoryArchivePass? _ArchivePass;

        private const int _MaxActionRecords = 2000;
        private const int _MaxErrorRecords = 200;
        private const string _CategoryDocks = "docks";
        private const string _CategoryBareRepos = "bareRepos";
        private const string _CategoryMissionLogs = "missionLogs";
        private const string _CategoryDiffs = "diffs";
        private const string _CategoryInstructions = "instructions";
        private const string _CategoryDockMetadata = "dockMetadata";
        private const string _CategoryLeases = "leases";
        private const string _CategoryIntegrationWorktrees = "integrationWorktrees";
        private const string _CategoryMergeQueueWorktrees = "mergeQueueWorktrees";
        private const string _CategoryTempArtifacts = "tempArtifacts";
        private const string _CategoryBackups = "backups";
        private const string _CategoryOperatorEvidence = "operatorEvidence";
        private const string _CategoryFinalMessages = "finalMessages";
        private const string _CategorySessionsPrefix = "captainSessions:";
        private const int _MaxReportedArchiveFailures = 5;
        private const string _ArchiveCapKeptReason = "the object store download or transaction cap is exceeded, so the run stopped verifying; kept locally";
        private const string _OperatorEvidenceFolder = "operator-evidence";
        private const string _KeepMarkerFile = ".armada-keep";

        private static readonly HashSet<string> _EvidencePayloadDirectoryNames = new HashSet<string>(StringComparer.Ordinal)
        {
            ".git",
            "bin",
            "obj",
            "node_modules"
        };

        private const string _ArchiveBundlePrefix = "armada-archive-bundle-";

        private static readonly string[] _TempArtifactPrefixes = new string[]
        {
            _ArchiveBundlePrefix,
            "armada-chk-",
            "armada_test_",
            "armada-code-index-",
            "armada-model-validation-",
            "armada_init_",
            "armada-iso-",
            "armada-backup-",
            "armada-restore-",
            "armada-test-home-",
            "armada_coverage_",
            "armada-tool-profile-",
            "armada-preview-",
            "armada-staleness-",
            "selfdeploy-provider-",
            "MSBuildTemp"
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Application settings.</param>
        /// <param name="logging">Logging module.</param>
        public DiskLifecycleService(DatabaseDriver database, ArmadaSettings settings, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Leases = new SiblingLeaseRegistry(_Logging, _Database, _Settings);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Scan Armada-owned storage and produce a byte-accounting report. Never deletes anything.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Disk-lifecycle report.</returns>
        public async Task<DiskLifecycleReport> ScanAsync(CancellationToken token = default)
        {
            return await RunPassAsync(deleteWhenEnabled: false, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Reconcile Armada-owned storage: scan, purge stale sibling leases, and when enabled
        /// and not in dry-run, delete eligible reclaimable items. Always emits an audit event.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Disk-lifecycle report.</returns>
        public async Task<DiskLifecycleReport> ReconcileAsync(CancellationToken token = default)
        {
            return await RunPassAsync(deleteWhenEnabled: true, token).ConfigureAwait(false);
        }

        /// <summary>
        /// True while a pass is running and has handled an item, written a bundle entry or finished an
        /// upload within the window. A background job that runs a pass past its stale limit is kept while
        /// this holds, so a long archive pass is not cancelled mid-upload; a pass that stops advancing is not.
        /// </summary>
        /// <param name="window">How recent the last step must be.</param>
        /// <returns>True when a pass is running and advanced within the window.</returns>
        public bool IsPassAdvancing(TimeSpan window)
        {
            if (_PassGate.CurrentCount > 0) return false;
            long last = Interlocked.Read(ref _LastProgressTicks);
            if (last == 0) return false;
            return DateTime.UtcNow.Ticks - last <= window.Ticks;
        }

        /// <summary>
        /// Test seam: invoked at the start of every pass while the pass holds the pass gate.
        /// </summary>
        internal Func<CancellationToken, Task>? OnPassStarted { get; set; }

        /// <summary>
        /// Test seam: returns the free bytes on the volume holding a path, or -1 when unknown.
        /// </summary>
        internal Func<string, long> FreeBytesProbe { get; set; } = ReadFreeBytes;

        /// <summary>
        /// The lease registry used to protect shared sibling worktrees from reclamation.
        /// </summary>
        public SiblingLeaseRegistry Leases
        {
            get => _Leases;
        }

        #endregion

        #region Private-Methods

        private async Task<DiskLifecycleReport> RunPassAsync(bool deleteWhenEnabled, CancellationToken token)
        {
            // One pass at a time: the health loop and the operator tool can both start a pass, and two
            // concurrent passes would reconcile the same leases and remove the same directories. A second
            // caller waits for the running pass and then scans the result it left behind.
            await _PassGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await RunPassUnderGateAsync(deleteWhenEnabled, token).ConfigureAwait(false);
            }
            finally
            {
                _CurrentReport = null;
                _PassGate.Release();
            }
        }

        private async Task<DiskLifecycleReport> RunPassUnderGateAsync(bool deleteWhenEnabled, CancellationToken token)
        {
            DiskLifecycleSettings section = _Settings.DiskLifecycle;
            bool delete = deleteWhenEnabled && section.Enabled && !section.DryRun;
            DiskLifecycleReport report = new DiskLifecycleReport
            {
                Enabled = section.Enabled,
                DryRun = !delete,
                ScannedUtc = DateTime.UtcNow
            };
            _CurrentReport = report;
            NoteProgress();
            report.FreeBytes = FreeBytesProbe(_Settings.LogDirectory);
            long lowSpaceBytes = (long)section.LowSpaceThresholdGb * 1024L * 1024L * 1024L;
            report.LowSpace = lowSpaceBytes > 0 && report.FreeBytes >= 0 && report.FreeBytes < lowSpaceBytes;
            if (report.LowSpace)
            {
                // Low-space mode only shortens evidence payload retention; most of a full disk can sit
                // outside the owned categories, so the operator is told rather than left to find out.
                _Logging.Warn(_Header + "low disk space: " + FormatBytes(report.FreeBytes) + " free on the log volume, below the "
                    + section.LowSpaceThresholdGb + " GB threshold; running in low-space mode");
                await EmitLowSpaceEventAsync(report.FreeBytes, section.LowSpaceThresholdGb, token).ConfigureAwait(false);
            }

            ArchiveSettings archive = _Settings.Archive;
            _HistoryMode = archive.ResolveHistoryMode();
            _ArchivePass = null;
            report.HistoryMode = _HistoryMode.ToString();
            if (archive.Enabled && !archive.IsConfigured)
                RecordError("archive is enabled but endpoint, bucket or credentialsFile is missing; history is " + (_HistoryMode == HistoryRetentionModeEnum.Keep ? "kept" : "deleted") + " without a copy");
            if (delete && _HistoryMode == HistoryRetentionModeEnum.Archive)
                _ArchivePass = new HistoryArchivePass(_ArchiveSinkFactory(archive), archive, report.ScannedUtc, section.ArchiveMaxFilesPerPass);

            if (OnPassStarted != null)
                await OnPassStarted(token).ConfigureAwait(false);

            try
            {
                await ReconcileLeasesAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordError("sibling lease reconciliation failed: " + ex.Message);
            }

            try
            {
                await ScanOrphanDocks(section, delete, report, token).ConfigureAwait(false);
                await ScanIntegrationWorktrees(section, delete, report, token).ConfigureAwait(false);
                await ScanMergeQueueWorktrees(section, delete, report, token).ConfigureAwait(false);
                // Operator evidence is the first history category: its folders are few and large, so a
                // backlog of small history files cannot hold them past the archive file bound for days.
                await ScanOperatorEvidence(section, delete, report, token).ConfigureAwait(false);
                await ScanMissionLogs(section, delete, report, token).ConfigureAwait(false);
                await ScanDiffSnapshots(section, delete, report, token).ConfigureAwait(false);
                await ScanInstructionSnapshots(section, delete, report, token).ConfigureAwait(false);
                await ScanDockMetadata(section, delete, report, token).ConfigureAwait(false);
                await ScanFinalMessages(section, delete, report, token).ConfigureAwait(false);
                await ScanTempArtifacts(section, delete, report, token).ConfigureAwait(false);
                await ScanBackups(section, delete, report, token).ConfigureAwait(false);
                await ScanExtraRetentionRoots(section, delete, report, token).ConfigureAwait(false);
                await ScanSessionRoots(delete, report, token).ConfigureAwait(false);
                ScanBareRepos(report);
            }
            catch (Exception ex)
            {
                RecordError("disk lifecycle scan failed: " + ex.Message);
            }

            await FinishArchivePassAsync(report, token).ConfigureAwait(false);
            FinalizeReport(report);
            await EmitReportEventAsync(report, delete, token).ConfigureAwait(false);
            return report;
        }

        private async Task ReconcileLeasesAsync(CancellationToken token)
        {
            TimeSpan grace = TimeSpan.FromHours(Math.Max(1, _Settings.DiskLifecycle.StaleLeaseGraceHours));
            int removed = await _Leases.ReconcileAsync(grace, token).ConfigureAwait(false);
            if (removed > 0)
            {
                _Logging.Info(_Header + "reconciled " + removed + " stale sibling lease file" + (removed == 1 ? "" : "s"));
            }
        }

        #region Scanners

        private async Task ScanOrphanDocks(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryDocks);
            string docksRoot = Path.GetFullPath(_Settings.DocksDirectory);
            if (!Directory.Exists(docksRoot))
            {
                report.Categories.Add(category);
                return;
            }

            // Sibling worktrees live directly under docks/<Vessel>/ and are shared; collect their
            // exact paths so the orphan sweep never touches them.
            HashSet<string> siblingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                List<Vessel> vessels = await _Database.Vessels.EnumerateAsync(token).ConfigureAwait(false);
                foreach (Vessel vessel in vessels)
                {
                    foreach (SiblingRepo sibling in vessel.GetSiblingRepos())
                    {
                    if (sibling == null || String.IsNullOrWhiteSpace(sibling.RelativePath))
                    {
                        continue;
                    }
                    // Mirror DockService: the sibling path resolves relative to a dock worktree
                    // directory (docks/<Vessel>/<dockId>/../<Sibling> -> docks/<Vessel>/<Sibling>), so
                    // the shared checkout lives one level above the per-mission dock directories.
                    string resolved = Path.GetFullPath(Path.Combine(Path.Combine(docksRoot, vessel.Name), "dock", sibling.RelativePath));
                    siblingPaths.Add(resolved);
                    }
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not enumerate sibling paths for orphan-dock scan: " + ex.Message);
            }

            // Active docks and docks preserved for pending missions are protected.
            HashSet<string> protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                List<Dock> activeDocks = await _Database.Docks.EnumerateAsync(token).ConfigureAwait(false);
                foreach (Dock dock in activeDocks)
                {
                    if (dock.Active && dock.HostCheckout == null && !String.IsNullOrWhiteSpace(dock.WorktreePath))
                    {
                        protectedPaths.Add(Path.GetFullPath(dock.WorktreePath!));
                    }
                }

                foreach (MissionStatusEnum status in new MissionStatusEnum[]
                {
                    MissionStatusEnum.Pending,
                    MissionStatusEnum.Assigned,
                    MissionStatusEnum.InProgress,
                    // Mid-pipeline states: the mission's dock is still in use while the DoD gate
                    // runs (WorkProduced), while a TestEngineer/Review stage works (Testing/Review),
                    // or while landing retries are possible (LandingFailed).
                    MissionStatusEnum.WorkProduced,
                    MissionStatusEnum.Testing,
                    MissionStatusEnum.Review,
                    MissionStatusEnum.LandingFailed
                })
                {
                    List<Mission> missions = await _Database.Missions.EnumerateByStatusAsync(status, token).ConfigureAwait(false);
                    foreach (Mission mission in missions)
                    {
                        if (String.IsNullOrWhiteSpace(mission.DockId))
                        {
                            continue;
                        }
                        Dock? dock = await _Database.Docks.ReadAsync(mission.DockId!, token).ConfigureAwait(false);
                        if (dock != null && dock.HostCheckout == null && !String.IsNullOrWhiteSpace(dock.WorktreePath))
                        {
                            protectedPaths.Add(Path.GetFullPath(dock.WorktreePath!));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Fail closed: with the protected set unreadable, any dock might belong to a live mission.
                // Scanning on would remove docks a landing, review or retry still reads.
                _Logging.Warn(_Header + "could not enumerate protected docks; the orphan-dock scan is skipped this pass: " + ex.Message);
                report.ErrorCount++;
                report.Errors.Add("docks: protected docks could not be read, so no dock was considered this pass: " + ex.Message);
                report.Categories.Add(category);
                return;
            }

            DateTime cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(1, section.OrphanDockGraceMinutes));

            foreach (string vesselDir in SafeEnumerateDirectories(docksRoot))
            {
                string vesselName = Path.GetFileName(vesselDir);
                if (vesselName.StartsWith("_", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (string candidate in SafeEnumerateDirectories(vesselDir))
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    string full = Path.GetFullPath(candidate);

                    // Fail closed: a symlink or a path outside the allowed roots is never
                    // classified reclaimable, so it can never be deleted.
                    if (!IsPathAllowed(full))
                    {
                        RecordAction(report, category, full, "skipped", "symlink or outside allowed roots");
                        continue;
                    }

                    long bytes = SafeGetDirectoryBytes(full);
                    category.TotalBytes += bytes;
                    category.TotalItems++;

                    // A nested dock (docks/<Vessel>/<dock>/<Vessel>) is judged by its checkout and
                    // reclaimed as a whole directory; a flat dock from the earlier layout IS its checkout.
                    string nestedCheckout = Path.Combine(full, vesselName);
                    string checkout = LooksLikeWorktree(nestedCheckout) ? nestedCheckout : full;

                    if (siblingPaths.Contains(full))
                    {
                        RecordAction(report, category, full, "protected", "shared sibling worktree");
                        category.ProtectedItems++;
                        continue;
                    }

                    if (protectedPaths.Contains(full) || protectedPaths.Contains(checkout))
                    {
                        RecordAction(report, category, full, "protected", "active dock or preserved for a live mission");
                        category.ProtectedItems++;
                        continue;
                    }

                    // Dock directories are named by dock id (docks/<Vessel>/<dockId>). A lease held
                    // by a definition-of-done gate pins the worktree even when the DB row has not yet
                    // been updated.
                    if (DockLeaseRegistry.IsHeld(Path.GetFileName(full)))
                    {
                        RecordAction(report, category, full, "protected", "dock lease held by an active gate");
                        category.ProtectedItems++;
                        continue;
                    }

                    if (!LooksLikeWorktree(checkout))
                    {
                        category.ProtectedItems++;
                        continue;
                    }

                    DateTime lastWrite = SafeGetLastWriteTimeUtc(checkout);
                    if (lastWrite > cutoff)
                    {
                        category.ProtectedItems++;
                        continue;
                    }

                    category.ReclaimableBytes += bytes;
                    category.ReclaimableItems++;

                    if (delete)
                    {
                        string? unsaved = await PreserveOrphanUncommittedWorkAsync(checkout, Path.GetFileName(full), token).ConfigureAwait(false);
                        if (unsaved != null)
                        {
                            RecordAction(report, category, full, "skipped", unsaved);
                            continue;
                        }

                        bool removed = await RemoveOrphanDockDirectoryAsync(full, token).ConfigureAwait(false);
                        RecordAction(report, category, full, removed ? "reclaimed" : "skipped",
                            removed ? "orphan dock directory past grace" : "directory removal failed");
                    }
                    else
                    {
                        RecordAction(report, category, full, "dry-run-reclaim", "orphan dock directory past grace");
                    }
                }
            }

            report.Categories.Add(category);
        }

        private async Task ScanIntegrationWorktrees(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryIntegrationWorktrees);
            string root = Path.Combine(_Settings.DocksDirectory, "_integration");
            TimeSpan retention = TimeSpan.FromHours(Math.Max(1, section.IntegrationWorktreeRetentionHours));
            await ScanWorktreeDirs(root, retention, delete, report, category, token).ConfigureAwait(false);
            report.Categories.Add(category);
        }

        private async Task ScanMergeQueueWorktrees(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryMergeQueueWorktrees);
            string root = Path.Combine(_Settings.DocksDirectory, "_merge-queue");
            TimeSpan retention = TimeSpan.FromHours(Math.Max(1, section.IntegrationWorktreeRetentionHours));

            HashSet<string> liveEntryIds = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                List<MergeEntry> entries = await _Database.MergeEntries.EnumerateAsync(token).ConfigureAwait(false);
                foreach (MergeEntry entry in entries)
                {
                    if (!IsTerminalMergeStatus(entry.Status))
                    {
                        liveEntryIds.Add(entry.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not enumerate merge entries for worktree scan: " + ex.Message);
            }

            foreach (string candidate in SafeEnumerateDirectories(root))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                string full = Path.GetFullPath(candidate);

                // Fail closed: a symlink can point at a real checkout, so it is never counted
                // reclaimable and never reaches removal.
                if (!IsPathAllowed(full))
                {
                    RecordAction(report, category, full, "skipped", "symlink or outside allowed roots");
                    continue;
                }

                string name = Path.GetFileName(full);
                if (liveEntryIds.Contains(name))
                {
                    category.ProtectedItems++;
                    continue;
                }

                DateTime lastWrite = SafeGetLastWriteTimeUtc(full);
                if (lastWrite > DateTime.UtcNow.Subtract(retention))
                {
                    category.ProtectedItems++;
                    continue;
                }

                long bytes = SafeGetDirectoryBytes(full);
                category.TotalBytes += bytes;
                category.TotalItems++;
                category.ReclaimableBytes += bytes;
                category.ReclaimableItems++;

                if (delete)
                {
                    bool removed = await RemoveDirectorySafeAsync(full, token).ConfigureAwait(false);
                    RecordAction(report, category, full, removed ? "reclaimed" : "skipped",
                        removed ? "leftover merge-queue worktree past retention" : "directory removal failed");
                }
                else
                {
                    RecordAction(report, category, full, "dry-run-reclaim", "leftover merge-queue worktree past retention");
                }
            }

            report.Categories.Add(category);
        }

        private async Task ScanMissionLogs(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryMissionLogs);
            string root = Path.Combine(_Settings.LogDirectory, "missions");
            TimeSpan retention = TimeSpan.FromDays(Math.Max(0, section.MissionLogRetentionDays));
            await ScanExpiredFiles(root, "*.log", retention, delete, report, category, token, history: true).ConfigureAwait(false);
            report.Categories.Add(category);
        }

        private async Task ScanDiffSnapshots(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryDiffs);
            string root = Path.Combine(_Settings.LogDirectory, "diffs");
            TimeSpan retention = TimeSpan.FromDays(Math.Max(0, section.DiffSnapshotRetentionDays));
            await ScanExpiredFiles(root, "*.diff", retention, delete, report, category, token, history: true).ConfigureAwait(false);
            report.Categories.Add(category);
        }

        private async Task ScanInstructionSnapshots(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryInstructions);
            string root = Path.Combine(_Settings.LogDirectory, "instructions");
            TimeSpan retention = TimeSpan.FromDays(Math.Max(0, section.InstructionRetentionDays));
            await ScanExpiredFiles(root, "*", retention, delete, report, category, token, history: true).ConfigureAwait(false);
            // Each mission also keeps the description and memory files its brief listed in folders beside
            // the snapshot (<mission>.mission, <mission>.memory). They are part of the same brief, so each
            // folder is one item under the same retention, aged by its newest file.
            await ScanExpiredHistoryFolders(root, retention, delete, report, category, "brief files folder expired by retention policy", token).ConfigureAwait(false);
            // The complete text of a cut handoff is brief material like an instruction snapshot, so the same
            // retention bounds it.
            string handoffs = Path.Combine(_Settings.LogDirectory, MissionService.HandoffFullTextDirectoryName);
            await ScanExpiredFiles(handoffs, "*.md", retention, delete, report, category, token, history: true).ConfigureAwait(false);
            report.Categories.Add(category);
        }

        private async Task ScanDockMetadata(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryDockMetadata);
            string root = Path.Combine(_Settings.LogDirectory, "docks");
            TimeSpan retention = TimeSpan.FromDays(Math.Max(0, section.DockMetadataRetentionDays));
            await ScanExpiredFiles(root, "*.start", retention, delete, report, category, token, history: true).ConfigureAwait(false);
            // Stage start records outlive their dock by design, so the same retention bounds them.
            string stageStarts = Path.Combine(_Settings.LogDirectory, MissionStageStartCommitResolver.StageStartDirectoryName);
            await ScanExpiredFiles(stageStarts, "*.start", retention, delete, report, category, token, history: true).ConfigureAwait(false);
            report.Categories.Add(category);
        }

        // A mission's final message is history like its log, so the mission-log retention bounds it.
        private async Task ScanFinalMessages(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryFinalMessages);
            string root = Path.Combine(_Settings.LogDirectory, "final-messages");
            TimeSpan retention = TimeSpan.FromDays(Math.Max(0, section.MissionLogRetentionDays));
            await ScanExpiredFiles(root, "*", retention, delete, report, category, token, history: true).ConfigureAwait(false);
            report.Categories.Add(category);
        }

        // Captain session transcripts sit outside Armada's own storage, so they are only ever freed
        // after a verified upload: without an active archive they are kept whatever the keep setting.
        private async Task ScanSessionRoots(bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            foreach (ArchiveSessionRoot configured in _Settings.Archive.SessionRoots)
            {
                if (token.IsCancellationRequested) break;
                if (configured == null) continue;
                if (String.IsNullOrWhiteSpace(configured.Path) || !Path.IsPathRooted(configured.Path))
                {
                    RecordError("archive session root skipped: path is empty or not absolute: '" + configured.Path + "'");
                    continue;
                }

                string root = Path.GetFullPath(configured.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string name = String.IsNullOrEmpty(configured.Name) ? Path.GetFileName(root) : configured.Name;
                if (String.IsNullOrEmpty(name))
                {
                    RecordError("archive session root skipped: a filesystem root cannot be a session root: " + configured.Path);
                    continue;
                }

                DiskLifecycleCategory category = NewCategory(_CategorySessionsPrefix + name);
                if (!Directory.Exists(root))
                {
                    report.Categories.Add(category);
                    continue;
                }
                if (IsSymlink(root))
                {
                    RecordError("archive session root skipped: the root is a symlink: " + root);
                    report.Categories.Add(category);
                    continue;
                }

                DateTime cutoff = DateTime.UtcNow.AddDays(-configured.RetentionDays);
                foreach (string file in SafeEnumerateFilesRecursive(root, configured.Pattern))
                {
                    if (token.IsCancellationRequested) break;
                    string full = Path.GetFullPath(file);
                    if (IsSymlink(full)) continue;
                    if (SafeGetFileLastWriteTimeUtc(full) > cutoff)
                    {
                        category.ProtectedItems++;
                        continue;
                    }

                    long bytes = SafeGetFileBytes(full);
                    category.TotalBytes += bytes;
                    category.TotalItems++;
                    await DisposeHistoryFileAsync(category, root, full, bytes, delete, report, "captain session transcript idle past " + configured.RetentionDays + " days",
                        neverDeleteWithoutArchive: true, token).ConfigureAwait(false);
                }
                report.Categories.Add(category);
            }
        }

        private async Task ScanTempArtifacts(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryTempArtifacts);
            string root = Path.GetTempPath();
            TimeSpan retention = TimeSpan.FromHours(Math.Max(1, section.TempArtifactRetentionHours));
            DateTime cutoff = DateTime.UtcNow.Subtract(retention);
            HashSet<string> liveCheckRunIds = await ReadLiveCheckRunIdsAsync(token).ConfigureAwait(false);

            foreach (string entry in SafeEnumerateFileSystemEntries(root))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                string name = Path.GetFileName(entry);
                if (!_TempArtifactPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    continue;
                }

                string full = Path.GetFullPath(entry);
                if (!IsPathAllowed(full))
                {
                    RecordAction(report, category, full, "skipped", "outside allowed roots");
                    report.SkippedItems++;
                    continue;
                }

                // A check executes in its own checkout and writes nothing to the directory itself, so
                // a long suite looks expired while it is still running. The directory names its check
                // run, so a check that has not finished keeps its checkout.
                string? liveCheckRunId = FindLiveCheckRunId(name, liveCheckRunIds);
                if (liveCheckRunId != null)
                {
                    category.ProtectedItems++;
                    RecordAction(report, category, full, "protected", "checkout of a check run that has not finished");
                    continue;
                }

                bool isDir = Directory.Exists(full);
                DateTime lastWrite = isDir ? SafeGetLastWriteTimeUtc(full) : SafeGetFileLastWriteTimeUtc(full);
                if (lastWrite > cutoff)
                {
                    category.ProtectedItems++;
                    continue;
                }

                long bytes = isDir ? SafeGetDirectoryBytes(full) : SafeGetFileBytes(full);
                category.TotalBytes += bytes;
                category.TotalItems++;
                category.ReclaimableBytes += bytes;
                category.ReclaimableItems++;

                if (delete)
                {
                    bool removed = isDir
                        ? await RemoveDirectorySafeAsync(full, token).ConfigureAwait(false)
                        : await RemoveFileSafeAsync(full, token).ConfigureAwait(false);
                    RecordAction(report, category, full, removed ? "reclaimed" : "skipped",
                        removed ? "Armada temp artifact past retention" : "removal failed");
                }
                else
                {
                    RecordAction(report, category, full, "dry-run-reclaim", "Armada temp artifact past retention");
                }
            }

            report.Categories.Add(category);
        }

        /// <summary>
        /// Read the ids of the check runs that have not reached a verdict. A Pending record counts:
        /// its checkout is created before the record leaves Pending, while it waits for the host slot.
        /// </summary>
        private async Task<HashSet<string>> ReadLiveCheckRunIdsAsync(CancellationToken token)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (CheckRunStatusEnum status in new CheckRunStatusEnum[] { CheckRunStatusEnum.Running, CheckRunStatusEnum.Pending })
            {
                CheckRunQuery query = new CheckRunQuery
                {
                    Status = status,
                    PageNumber = 1,
                    PageSize = 200,
                    IncludeOutput = false
                };

                try
                {
                    while (true)
                    {
                        EnumerationResult<CheckRun> page = await _Database.CheckRuns.EnumerateAsync(query, token).ConfigureAwait(false);
                        foreach (CheckRun run in page.Objects)
                        {
                            if (run != null && !String.IsNullOrWhiteSpace(run.Id)) ids.Add(run.Id);
                        }

                        if (page.Objects.Count < query.PageSize || query.PageNumber >= page.TotalPages) break;
                        query.PageNumber++;
                    }
                }
                catch (Exception ex)
                {
                    // Fail closed: an unreadable live set protects every checkout this pass.
                    _Logging.Warn(_Header + "could not enumerate " + status + " check runs; temp checkouts are protected this pass: " + ex.Message);
                    ids.Add(String.Empty);
                }
            }

            return ids;
        }

        /// <summary>
        /// The live check run a temp directory belongs to, or null when none does. An unreadable live
        /// set (marked by the empty id) protects every checkout.
        /// </summary>
        private static string? FindLiveCheckRunId(string directoryName, HashSet<string> liveCheckRunIds)
        {
            if (!directoryName.StartsWith(CheckRunService.CheckoutDirectoryPrefix, StringComparison.Ordinal)) return null;
            if (liveCheckRunIds.Contains(String.Empty)) return String.Empty;

            string remainder = directoryName.Substring(CheckRunService.CheckoutDirectoryPrefix.Length);
            foreach (string id in liveCheckRunIds)
            {
                if (id.Length > 0 && remainder.StartsWith(id, StringComparison.Ordinal)) return id;
            }

            return null;
        }

        private async Task ScanBackups(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryBackups);
            string root = Path.Combine(_Settings.DataDirectory, "backups");
            if (!Directory.Exists(root))
            {
                report.Categories.Add(category);
                return;
            }

            TimeSpan retention = TimeSpan.FromDays(Math.Max(0, section.BackupRetentionDays));
            DateTime cutoff = DateTime.UtcNow.Subtract(retention);
            int minKeep = Math.Max(1, section.MinBackupCount);

            List<string> archives = new List<string>();
            foreach (string entry in SafeEnumerateFiles(root, "*.zip"))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }
                archives.Add(entry);
            }
            archives.Sort((a, b) => SafeGetFileLastWriteTimeUtc(b).CompareTo(SafeGetFileLastWriteTimeUtc(a)));

            int kept = 0;
            foreach (string archive in archives)
            {
                long bytes = SafeGetFileBytes(archive);
                category.TotalBytes += bytes;
                category.TotalItems++;

                bool protectedAsNewest = kept < minKeep;
                if (protectedAsNewest || SafeGetFileLastWriteTimeUtc(archive) > cutoff)
                {
                    kept++;
                    category.ProtectedItems++;
                    continue;
                }

                category.ReclaimableBytes += bytes;
                category.ReclaimableItems++;

                if (delete)
                {
                    bool removed = await RemoveFileSafeAsync(archive, token).ConfigureAwait(false);
                    RecordAction(report, category, archive, removed ? "reclaimed" : "skipped",
                        removed ? "backup past retention (newest " + minKeep + " protected)" : "removal failed");
                }
                else
                {
                    RecordAction(report, category, archive, "dry-run-reclaim", "backup past retention (newest " + minKeep + " protected)");
                }
            }

            report.Categories.Add(category);
        }

        // Operator proof runs keep their receipts, logs and full clones here. A folder with no write
        // anywhere inside it for the payload retention loses its clones and build outputs; one idle for
        // the folder retention, when one is set, is removed whole. A folder holding the keep marker is never touched.
        private async Task ScanOperatorEvidence(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryOperatorEvidence);
            string root = Path.Combine(_Settings.LogDirectory, _OperatorEvidenceFolder);
            if (!Directory.Exists(root))
            {
                report.Categories.Add(category);
                return;
            }

            int payloadHours = report.LowSpace
                ? Math.Min(section.LowSpacePayloadRetentionHours, section.OperatorEvidencePayloadRetentionHours)
                : section.OperatorEvidencePayloadRetentionHours;
            DateTime payloadCutoff = DateTime.UtcNow.AddHours(-payloadHours);
            bool removeWholeFolders = section.OperatorEvidenceRetentionDays > 0;
            DateTime folderCutoff = DateTime.UtcNow.AddDays(-section.OperatorEvidenceRetentionDays);
            if (report.LowSpace) category.Note = "low-space mode: payload retention " + payloadHours + "h";

            foreach (string candidate in SafeEnumerateDirectories(root))
            {
                if (token.IsCancellationRequested) break;

                string full = Path.GetFullPath(candidate);
                if (IsSymlink(full)) continue;
                long bytes = SafeGetDirectoryBytes(full);
                category.TotalBytes += bytes;
                category.TotalItems++;

                if (File.Exists(Path.Combine(full, _KeepMarkerFile)))
                {
                    category.ProtectedItems++;
                    continue;
                }

                DateTime newest = NewestWriteTimeUtc(full, payloadCutoff);
                if (newest > payloadCutoff)
                {
                    category.ProtectedItems++;
                    continue;
                }

                if (removeWholeFolders && newest <= folderCutoff)
                {
                    string folderReason = "operator evidence idle past " + section.OperatorEvidenceRetentionDays + " days";
                    if (_HistoryMode != HistoryRetentionModeEnum.Delete)
                    {
                        await DisposeHistoryFolderAsync(category, root, full, bytes, delete, report, folderReason, skipPayload: true, token).ConfigureAwait(false);
                        continue;
                    }

                    category.ReclaimableBytes += bytes;
                    category.ReclaimableItems++;
                    if (delete)
                    {
                        bool removed = await RemoveDirectorySafeAsync(full, token).ConfigureAwait(false);
                        RecordAction(report, category, full, removed ? "reclaimed" : "skipped", removed ? folderReason : "directory removal failed");
                    }
                    else
                    {
                        RecordAction(report, category, full, "dry-run-reclaim", folderReason);
                    }
                    continue;
                }

                List<string> payload = FindPayloadDirectories(full);
                if (payload.Count == 0)
                {
                    category.ProtectedItems++;
                    continue;
                }

                string payloadReason = "operator evidence payload (clones and build outputs) idle past " + payloadHours + " hours; receipts and logs kept";
                foreach (string directory in payload)
                {
                    long payloadBytes = SafeGetDirectoryBytes(directory);
                    category.ReclaimableBytes += payloadBytes;
                    category.ReclaimableItems++;
                    if (delete)
                    {
                        bool removed = await RemoveDirectorySafeAsync(directory, token).ConfigureAwait(false);
                        RecordAction(report, category, directory, removed ? "reclaimed" : "skipped", removed ? payloadReason : "directory removal failed");
                    }
                    else
                    {
                        RecordAction(report, category, directory, "dry-run-reclaim", payloadReason);
                    }
                }
            }

            report.Categories.Add(category);
        }

        // Operator directories outside Armada's own storage (deploy dumps, rehearsal scratch) are kept
        // bounded per configured root. Each direct child is one item, aged by the newest write anywhere
        // inside it; the newest children and any child holding the keep marker are never touched.
        private async Task ScanExtraRetentionRoots(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            foreach (DiskLifecycleRetentionRoot configured in section.ExtraRetentionRoots)
            {
                if (token.IsCancellationRequested) break;
                if (configured == null) continue;
                await ScanExtraRetentionRoot(configured, delete, report, token).ConfigureAwait(false);
            }
        }

        private async Task ScanExtraRetentionRoot(DiskLifecycleRetentionRoot configured, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(configured.Path) || !Path.IsPathRooted(configured.Path))
            {
                RecordError("extra retention root skipped: path is empty or not absolute: '" + configured.Path + "'");
                return;
            }

            string root = Path.GetFullPath(configured.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string leaf = Path.GetFileName(root);
            if (String.IsNullOrEmpty(leaf))
            {
                RecordError("extra retention root skipped: a filesystem root cannot be a retention root: " + configured.Path);
                return;
            }

            DiskLifecycleCategory category = NewCategory("extraRoot:" + leaf);
            if (!Directory.Exists(root))
            {
                report.Categories.Add(category);
                return;
            }

            if (IsSymlink(root))
            {
                RecordError("extra retention root skipped: the root is a symlink: " + root);
                report.Categories.Add(category);
                return;
            }

            DateTime cutoff = DateTime.UtcNow.AddHours(-configured.RetentionHours);
            List<DiskLifecycleRetentionItem> items = new List<DiskLifecycleRetentionItem>();
            foreach (string candidate in SafeEnumerateFileSystemEntries(root))
            {
                if (token.IsCancellationRequested) break;

                string full = Path.GetFullPath(candidate);
                if (IsSymlink(full))
                {
                    RecordAction(report, category, full, "skipped", "symlink");
                    continue;
                }

                if (!String.Equals(Path.GetDirectoryName(full), root, StringComparison.Ordinal))
                {
                    RecordAction(report, category, full, "skipped", "not a direct child of the retention root");
                    continue;
                }

                bool isDirectory = Directory.Exists(full);
                DiskLifecycleRetentionItem item = new DiskLifecycleRetentionItem
                {
                    Path = full,
                    IsDirectory = isDirectory,
                    Bytes = isDirectory ? SafeGetDirectoryBytes(full) : SafeGetFileBytes(full),
                    NewestWriteUtc = isDirectory ? NewestWriteTimeUtc(full, DateTime.MaxValue) : SafeGetFileLastWriteTimeUtc(full),
                    Kept = isDirectory && File.Exists(Path.Combine(full, _KeepMarkerFile))
                };
                category.TotalBytes += item.Bytes;
                category.TotalItems++;
                items.Add(item);
            }

            items.Sort((a, b) => b.NewestWriteUtc.CompareTo(a.NewestWriteUtc));
            int newestKept = 0;
            string reason = "idle past " + configured.RetentionHours + " hours (newest " + configured.KeepNewest + " kept)";
            foreach (DiskLifecycleRetentionItem item in items)
            {
                if (token.IsCancellationRequested) break;

                if (item.Kept)
                {
                    category.ProtectedItems++;
                    continue;
                }

                if (newestKept < configured.KeepNewest)
                {
                    newestKept++;
                    category.ProtectedItems++;
                    continue;
                }

                if (item.NewestWriteUtc > cutoff)
                {
                    category.ProtectedItems++;
                    continue;
                }

                category.ReclaimableBytes += item.Bytes;
                category.ReclaimableItems++;
                if (delete)
                {
                    bool removed = item.IsDirectory
                        ? await RemoveDirectorySafeAsync(item.Path, token).ConfigureAwait(false)
                        : await RemoveFileSafeAsync(item.Path, token).ConfigureAwait(false);
                    RecordAction(report, category, item.Path, removed ? "reclaimed" : "skipped", removed ? reason : "removal failed");
                }
                else
                {
                    RecordAction(report, category, item.Path, "dry-run-reclaim", reason);
                }
            }

            report.Categories.Add(category);
        }

        private void ScanBareRepos(DiskLifecycleReport report)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryBareRepos);
            string root = Path.GetFullPath(_Settings.ReposDirectory);
            if (!Directory.Exists(root))
            {
                report.Categories.Add(category);
                return;
            }

            foreach (string dir in SafeEnumerateDirectories(root))
            {
                long bytes = SafeGetDirectoryBytes(dir);
                category.TotalBytes += bytes;
                category.TotalItems++;
            }
            category.ProtectedItems = category.TotalItems;
            category.Note = "report-only; bare repositories are never reclaimed automatically";

            report.Categories.Add(category);
        }

        private async Task ScanWorktreeDirs(string root, TimeSpan retention, bool delete, DiskLifecycleReport report, DiskLifecycleCategory category, CancellationToken token)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow.Subtract(retention);
            foreach (string candidate in SafeEnumerateDirectories(root))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                string full = Path.GetFullPath(candidate);
                if (!IsPathAllowed(full))
                {
                    RecordAction(report, category, full, "skipped", "symlink or outside allowed roots");
                    continue;
                }

                DateTime lastWrite = SafeGetLastWriteTimeUtc(full);
                if (lastWrite > cutoff)
                {
                    category.ProtectedItems++;
                    continue;
                }

                long bytes = SafeGetDirectoryBytes(full);
                category.TotalBytes += bytes;
                category.TotalItems++;
                category.ReclaimableBytes += bytes;
                category.ReclaimableItems++;

                if (delete)
                {
                    bool removed = await RemoveDirectorySafeAsync(full, token).ConfigureAwait(false);
                    RecordAction(report, category, full, removed ? "reclaimed" : "skipped",
                        removed ? "leftover worktree past retention" : "directory removal failed");
                }
                else
                {
                    RecordAction(report, category, full, "dry-run-reclaim", "leftover worktree past retention");
                }
            }
        }

        private async Task ScanExpiredFiles(string root, string pattern, TimeSpan retention, bool delete, DiskLifecycleReport report, DiskLifecycleCategory category, CancellationToken token, bool history = false)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            DateTime cutoff = DateTime.UtcNow.Subtract(retention);
            foreach (string file in SafeEnumerateFiles(root, pattern))
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                string full = Path.GetFullPath(file);
                DateTime lastWrite = SafeGetFileLastWriteTimeUtc(full);
                if (lastWrite > cutoff)
                {
                    category.ProtectedItems++;
                    continue;
                }

                long bytes = SafeGetFileBytes(full);
                category.TotalBytes += bytes;
                category.TotalItems++;
                if (history)
                {
                    await DisposeHistoryFileAsync(category, root, full, bytes, delete, report, "expired by retention policy", neverDeleteWithoutArchive: false, token).ConfigureAwait(false);
                    continue;
                }

                category.ReclaimableBytes += bytes;
                category.ReclaimableItems++;

                if (delete)
                {
                    bool removed = await RemoveFileSafeAsync(full, token).ConfigureAwait(false);
                    RecordAction(report, category, full, removed ? "reclaimed" : "skipped",
                        removed ? "expired by retention policy" : "removal failed");
                }
                else
                {
                    RecordAction(report, category, full, "dry-run-reclaim", "expired by retention policy");
                }
            }
        }

        // Each direct subfolder of a history root is one item, aged by the newest file anywhere inside it.
        private async Task ScanExpiredHistoryFolders(string root, TimeSpan retention, bool delete, DiskLifecycleReport report, DiskLifecycleCategory category, string reason, CancellationToken token)
        {
            if (!Directory.Exists(root)) return;

            DateTime cutoff = DateTime.UtcNow.Subtract(retention);
            List<string> folders = SafeEnumerateDirectories(root);
            folders.Sort(StringComparer.Ordinal);
            foreach (string folder in folders)
            {
                if (token.IsCancellationRequested) break;

                string full = Path.GetFullPath(folder);
                if (IsSymlink(full))
                {
                    RecordAction(report, category, full, "skipped", "symlink; never followed");
                    continue;
                }

                if (NewestWriteTimeUtc(full, cutoff) > cutoff)
                {
                    category.ProtectedItems++;
                    continue;
                }

                long bytes = SafeGetDirectoryBytes(full);
                category.TotalBytes += bytes;
                category.TotalItems++;
                if (_HistoryMode != HistoryRetentionModeEnum.Delete)
                {
                    await DisposeHistoryFolderAsync(category, root, full, bytes, delete, report, reason, skipPayload: false, token).ConfigureAwait(false);
                    continue;
                }

                category.ReclaimableBytes += bytes;
                category.ReclaimableItems++;
                if (!delete)
                {
                    RecordAction(report, category, full, "dry-run-reclaim", reason);
                    continue;
                }

                string? failure = FreeHistoryDirectory(full);
                if (failure != null) RecordError(category.Category + ": could not remove " + full + ": " + failure);
                RecordAction(report, category, full, failure == null ? "reclaimed" : "skipped", failure == null ? reason : "directory removal failed: " + failure);
            }
        }

        #endregion

        #region Guards and Helpers

        internal bool IsPathAllowed(string fullPath)
        {
            if (IsSymlink(fullPath))
            {
                return false;
            }

            foreach (string root in ResolveAllowedRoots())
            {
                if (PathContainment.IsWithin(root, fullPath)) return true;
            }
            return false;
        }

        private List<string> ResolveAllowedRoots()
        {
            List<string> roots = new List<string>
            {
                Path.GetFullPath(_Settings.LogDirectory),
                Path.GetFullPath(_Settings.DocksDirectory),
                Path.GetFullPath(_Settings.ReposDirectory),
                Path.GetFullPath(Path.Combine(_Settings.DataDirectory, "backups")),
                Path.GetFullPath(Path.GetTempPath())
            };

            foreach (string extra in _Settings.DiskLifecycle.AllowedRoots)
            {
                if (!String.IsNullOrWhiteSpace(extra))
                {
                    roots.Add(Path.GetFullPath(extra.Trim()));
                }
            }

            foreach (ArchiveSessionRoot sessionRoot in _Settings.Archive.SessionRoots)
            {
                if (sessionRoot != null && Path.IsPathRooted(sessionRoot.Path))
                {
                    roots.Add(Path.GetFullPath(sessionRoot.Path));
                }
            }

            foreach (DiskLifecycleRetentionRoot retentionRoot in _Settings.DiskLifecycle.ExtraRetentionRoots)
            {
                if (retentionRoot != null && Path.IsPathRooted(retentionRoot.Path))
                {
                    roots.Add(Path.GetFullPath(retentionRoot.Path));
                }
            }
            return roots;
        }

        private static bool IsSymlink(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return true;
            }
        }

        private static bool LooksLikeWorktree(string path)
        {
            string gitMarker = Path.Combine(path, ".git");
            return Directory.Exists(gitMarker) || File.Exists(gitMarker);
        }

        /// <summary>
        /// Saves an orphan dock checkout's uncommitted work with the shared dock work snapshot (a
        /// <c>recover/{dockDir}-{n}</c> branch in its vessel repository) before the directory is reclaimed.
        /// Returns null when removal may proceed, or the reason the directory is kept.
        /// </summary>
        private async Task<string?> PreserveOrphanUncommittedWorkAsync(string checkout, string dockDirName, CancellationToken token)
        {
            DockWorkSnapshot snapshot;
            try
            {
                snapshot = await new GitService(_Logging).SnapshotDockWorkAsync(checkout, dockDirName,
                    "Armada snapshot of an orphan dock before disk reclaim", token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                snapshot = DockWorkSnapshot.Failed(ex.Message);
            }

            if (snapshot.Status == DockWorkSnapshotStatusEnum.NoChanges) return null;
            if (snapshot.HasRef)
            {
                _Logging.Info(_Header + "saved uncommitted work of orphan dock " + checkout + " to " + snapshot.RefName + " at " + snapshot.CommitSha);
                return null;
            }

            _Logging.Warn(_Header + "work_snapshot_failed for orphan dock " + checkout + "; the directory is kept: " + snapshot.Message);
            return "uncommitted work was not saved, so the directory is kept: " + snapshot.Message;
        }

        private async Task<bool> RemoveOrphanDockDirectoryAsync(string path, CancellationToken token)
        {
            if (!IsPathAllowed(path))
            {
                _Logging.Warn(_Header + "refusing to remove orphan dock outside allowed roots: " + path);
                return false;
            }

            // Unregister from the owning bare repo first so no stale worktree registration remains.
            string? gitdirFile = Path.Combine(path, ".git");
            if (File.Exists(gitdirFile))
            {
                try
                {
                    string content = await File.ReadAllTextAsync(gitdirFile, token).ConfigureAwait(false);
                    string marker = "gitdir:";
                    int idx = content.IndexOf(marker, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        string commonGitDir = content.Substring(idx + marker.Length).Trim();
                        string? commonDir = Path.GetDirectoryName(commonGitDir);
                        if (!String.IsNullOrEmpty(commonDir))
                        {
                            await RunGitAsync(commonDir, TimeSpan.FromMinutes(2), token, "worktree", "remove", "--force", path).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not unregister orphan worktree " + path + ": " + ex.Message);
                }
            }

            bool removed = await RemoveDirectorySafeAsync(path, token).ConfigureAwait(false);
            if (removed)
            {
                // Clean any stale registration for the removed path.
                try
                {
                    string? parentGitDir = FindOwningBareRepo(path);
                    if (!String.IsNullOrEmpty(parentGitDir))
                    {
                        await RunGitAsync(parentGitDir, TimeSpan.FromMinutes(2), token, "worktree", "prune").ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _Logging.Debug(_Header + "worktree prune after orphan removal skipped: " + ex.Message);
                }
            }
            return removed;
        }

        private string? FindOwningBareRepo(string worktreePath)
        {
            string gitdirFile = Path.Combine(worktreePath, ".git");
            if (!File.Exists(gitdirFile))
            {
                return null;
            }

            try
            {
                string content = File.ReadAllText(gitdirFile);
                string marker = "gitdir:";
                int idx = content.IndexOf(marker, StringComparison.Ordinal);
                if (idx < 0)
                {
                    return null;
                }
                string commonGitDir = content.Substring(idx + marker.Length).Trim();
                string? commonDir = Path.GetDirectoryName(commonGitDir);
                return String.IsNullOrEmpty(commonDir) ? null : Path.GetFullPath(commonDir);
            }
            catch
            {
                return null;
            }
        }

        private async Task<bool> RemoveDirectorySafeAsync(string path, CancellationToken token)
        {
            if (!IsPathAllowed(path))
            {
                _Logging.Warn(_Header + "refusing to remove path outside allowed roots: " + path);
                return false;
            }

            try
            {
                string? modeFailure = null;
                if (OperatingSystem.IsWindows())
                {
                    foreach (string file in SafeEnumerateFiles(path, "*"))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    }
                }
                else
                {
                    // A read-only folder inside the item would refuse removal of its entries.
                    modeFailure = MakeDirectoriesOwnerWritable(path);
                }
                if (modeFailure != null) _Logging.Warn(_Header + "could not make every folder writable before removing " + path + ": " + modeFailure);
                Directory.Delete(path, true);
                return true;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "directory removal failed for " + path + ": " + ex.Message);
                return false;
            }
        }

        private async Task<bool> RemoveFileSafeAsync(string path, CancellationToken token)
        {
            if (!IsPathAllowed(path))
            {
                _Logging.Warn(_Header + "refusing to remove file outside allowed roots: " + path);
                return false;
            }

            try
            {
                if (OperatingSystem.IsWindows())
                {
                    try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
                }
                File.Delete(path);
                return true;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "file removal failed for " + path + ": " + ex.Message);
                return false;
            }
        }

        private async Task<int> RunGitAsync(string workingDirectory, TimeSpan timeout, CancellationToken token, params string[] args)
        {
            BoundedProcessRequest request = new BoundedProcessRequest(GitProcessStartInfo.Create(workingDirectory, args), timeout)
            {
                OutputLimitBytes = 1024 * 1024
            };
            BoundedProcessResult result = await BoundedProcessRunner.RunAsync(request, token).ConfigureAwait(false);
            if (result.KillError != null)
                _Logging.Warn(_Header + "could not kill git " + String.Join(" ", args) + "; it may still be running: " + result.KillError);
            if (result.Cancelled)
            {
                token.ThrowIfCancellationRequested();
                throw new OperationCanceledException(token);
            }
            if (result.TimedOut)
                throw new TimeoutException("git " + String.Join(" ", args) + " timed out after " + timeout.TotalSeconds.ToString("F0") + " seconds");
            return result.ExitCode ?? -1;
        }

        private void NoteProgress()
        {
            Interlocked.Exchange(ref _LastProgressTicks, DateTime.UtcNow.Ticks);
        }

        private void RecordAction(DiskLifecycleReport report, DiskLifecycleCategory category, string path, string disposition, string reason)
        {
            NoteProgress();
            if (report.Actions.Count >= _MaxActionRecords)
            {
                return;
            }

            DiskLifecycleAction action = new DiskLifecycleAction
            {
                Category = category.Category,
                Path = path,
                Disposition = disposition,
                Reason = reason
            };
            report.Actions.Add(action);
        }

        private static string ArchiveCategory(DiskLifecycleCategory category)
        {
            return category.Category.Replace(':', '-');
        }

        // Frees one expired history file only once a verified copy exists. Without an archive the file
        // is kept, unless KeepHistoryWithoutArchive is off and the category allows a plain delete.
        private async Task DisposeHistoryFileAsync(DiskLifecycleCategory category, string root, string full, long bytes, bool delete, DiskLifecycleReport report, string reason, bool neverDeleteWithoutArchive, CancellationToken token)
        {
            if (ArchiveDenyList.IsDenied(Path.GetRelativePath(root, full), _Settings.Archive.DenyPatterns))
            {
                category.ProtectedItems++;
                RecordAction(report, category, full, "kept", "matches the archive deny list; never uploaded and never freed");
                return;
            }

            if (_HistoryMode == HistoryRetentionModeEnum.Keep || (_HistoryMode == HistoryRetentionModeEnum.Delete && neverDeleteWithoutArchive))
            {
                category.ProtectedItems++;
                RecordAction(report, category, full, "kept", reason + "; kept because no archive is configured");
                return;
            }

            category.ReclaimableBytes += bytes;
            category.ReclaimableItems++;
            if (!delete)
            {
                RecordAction(report, category, full, "dry-run-reclaim", _HistoryMode == HistoryRetentionModeEnum.Archive ? reason + "; would archive, then free" : reason);
                return;
            }

            if (_HistoryMode == HistoryRetentionModeEnum.Delete)
            {
                bool removedPlain = await RemoveFileSafeAsync(full, token).ConfigureAwait(false);
                RecordAction(report, category, full, removedPlain ? "reclaimed" : "skipped", removedPlain ? reason : "removal failed");
                return;
            }

            HistoryArchivePass pass = _ArchivePass ?? throw new InvalidOperationException("archive mode without an archive pass");
            if (pass.CapReached)
            {
                // The store refused a read-back for a cap earlier in this run; keep the file locally
                // without touching the store again, so the run does not send one more capped request.
                pass.SkipForCap();
                RecordAction(report, category, full, "kept", reason + "; " + _ArchiveCapKeptReason);
                return;
            }

            if (pass.Exhausted)
            {
                pass.Defer();
                RecordAction(report, category, full, "deferred", reason + "; the pass reached its archive file bound, so the next pass archives it");
                return;
            }

            ArchivePutResult put = await pass.ArchiveFileAsync(ArchiveCategory(category), root, full, token).ConfigureAwait(false);
            NoteProgress();
            if (put.FailureCode == ArchivePutResult.CapExceededCode)
            {
                pass.SkipForCap();
                RecordAction(report, category, full, "kept", reason + "; " + _ArchiveCapKeptReason);
                return;
            }

            if (!put.Verified)
            {
                RecordAction(report, category, full, "skipped", "archive upload not verified (" + put.FailureCode + "); kept");
                return;
            }

            string? freeFailure = FreeHistoryFile(full);
            if (freeFailure != null)
            {
                pass.RecordFreeFailure(full, freeFailure);
                RecordError(category.Category + ": archived to " + put.Key + " but could not be freed: " + full + ": " + freeFailure);
            }
            RecordAction(report, category, full, freeFailure == null ? "reclaimed" : "skipped",
                freeFailure == null ? reason + "; archived to " + put.Key + ", then freed" : "archived to " + put.Key + "; removal failed: " + freeFailure);
        }

        // A history folder (an idle operator-evidence folder, a mission's brief files) is archived file by
        // file before it is freed: every file, or with skipPayload every file outside the payload
        // directories (clones and build outputs). Any deny-listed file or failed upload keeps the whole
        // folder. A folder whose files are all verified but that cannot be removed is named in the
        // report's errors and the pass's failure event, and the next pass retries it.
        private async Task DisposeHistoryFolderAsync(DiskLifecycleCategory category, string root, string full, long bytes, bool delete, DiskLifecycleReport report, string reason, bool skipPayload, CancellationToken token)
        {
            if (_HistoryMode == HistoryRetentionModeEnum.Keep)
            {
                category.ProtectedItems++;
                RecordAction(report, category, full, "kept", reason + "; kept because no archive is configured");
                return;
            }

            List<string> payloadDirectories = skipPayload ? FindPayloadDirectories(full) : new List<string>();
            HashSet<string> payload = new HashSet<string>(payloadDirectories.Select(d => Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar), StringComparer.Ordinal);
            List<string> files = new List<string>();
            List<string> denied = new List<string>();
            long deniedBytes = 0;
            foreach (string file in SafeEnumerateFilesRecursive(full, "*"))
            {
                string fileFull = Path.GetFullPath(file);
                if (payload.Any(prefix => fileFull.StartsWith(prefix, StringComparison.Ordinal))) continue;
                if (IsSymlink(fileFull)) continue;
                if (ArchiveDenyList.IsDenied(Path.GetRelativePath(root, fileFull), _Settings.Archive.DenyPatterns))
                {
                    // A deny-listed file is never uploaded and never freed, but it does not hold the
                    // rest of its folder: the other files are archived and freed around it.
                    denied.Add(fileFull);
                    deniedBytes += SafeGetFileBytes(fileFull);
                    continue;
                }
                files.Add(fileFull);
            }

            if (denied.Count > 0 && files.Count == 0)
            {
                category.ProtectedItems++;
                RecordAction(report, category, full, "kept", reason + "; holds only deny-listed files (" + Path.GetRelativePath(full, denied[0])
                    + (denied.Count > 1 ? " and " + (denied.Count - 1) + " more" : "") + "), which are never uploaded and never freed");
                return;
            }

            string deniedNote = denied.Count == 0 ? "" : "; " + denied.Count + " deny-listed files (" + Path.GetRelativePath(full, denied[0]) + (denied.Count > 1 ? ", ..." : "") + ") stay in place";
            category.ReclaimableBytes += Math.Max(0, bytes - deniedBytes);
            category.ReclaimableItems++;
            bool bundle = files.Count >= _Settings.DiskLifecycle.ArchiveBundleMinFiles;
            if (!delete)
            {
                RecordAction(report, category, full, "dry-run-reclaim", reason + "; would archive " + files.Count + " files" + (bundle ? " as one bundle" : "") + ", then free" + deniedNote);
                return;
            }

            HistoryArchivePass pass = _ArchivePass ?? throw new InvalidOperationException("archive mode without an archive pass");
            if (pass.CapReached)
            {
                pass.SkipForCap();
                RecordAction(report, category, full, "kept", reason + "; " + _ArchiveCapKeptReason);
                return;
            }

            if (pass.Exhausted)
            {
                pass.Defer();
                RecordAction(report, category, full, "deferred", reason + "; the pass reached its archive file bound, so the next pass archives it");
                return;
            }

            if (bundle)
            {
                ArchivePutResult put = await ArchiveFolderBundleAsync(pass, ArchiveCategory(category), root, full, files, token).ConfigureAwait(false);
                if (!KeepFolderAfterPut(pass, category, report, full, reason, put, "the bundle of " + files.Count + " files")) return;
            }
            else
            {
                foreach (string file in files)
                {
                    if (token.IsCancellationRequested) return;
                    ArchivePutResult put = await pass.ArchiveFileAsync(ArchiveCategory(category), root, file, token).ConfigureAwait(false);
                    NoteProgress();
                    if (!KeepFolderAfterPut(pass, category, report, full, reason, put, Path.GetRelativePath(full, file))) return;
                }
            }

            string? freeFailure = denied.Count == 0 ? FreeHistoryDirectory(full) : FreeHistoryFolderAround(full, files, payloadDirectories, denied);
            if (freeFailure != null)
            {
                pass.RecordFreeFailure(full, freeFailure);
                RecordError(category.Category + ": " + files.Count + " files archived but the folder could not be freed: " + full + ": " + freeFailure);
            }
            RecordAction(report, category, full, freeFailure == null ? "reclaimed" : "skipped",
                freeFailure == null
                    ? reason + "; " + files.Count + " files archived" + (bundle ? " as one bundle" : "") + ", then freed" + deniedNote
                    : files.Count + " files archived; directory removal failed: " + freeFailure);
        }

        // True when an upload of a folder's content verified, so the folder may go on; otherwise the folder
        // is kept, the outcome is recorded, and false is returned.
        private bool KeepFolderAfterPut(HistoryArchivePass pass, DiskLifecycleCategory category, DiskLifecycleReport report, string full, string reason, ArchivePutResult put, string what)
        {
            if (put.FailureCode == ArchivePutResult.CapExceededCode)
            {
                pass.SkipForCap();
                RecordAction(report, category, full, "kept", reason + "; " + _ArchiveCapKeptReason);
                return false;
            }

            if (!put.Verified)
            {
                RecordAction(report, category, full, "skipped", "archive upload of " + what + " not verified (" + put.FailureCode + "); folder kept");
                return false;
            }

            return true;
        }

        // Packs a folder's archivable files into one gzip-compressed tar under the system temp directory,
        // uploads it as one object keyed by the folder's relative path plus ".tar.gz", and removes the
        // temporary bundle whatever the outcome. Entry names are relative to the folder.
        private async Task<ArchivePutResult> ArchiveFolderBundleAsync(HistoryArchivePass pass, string category, string root, string full, List<string> files, CancellationToken token)
        {
            string relative = Path.GetRelativePath(root, full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".tar.gz";
            string bundlePath = Path.Combine(Path.GetTempPath(), _ArchiveBundlePrefix + Guid.NewGuid().ToString("N") + ".tar.gz");
            DateTime newest = DateTime.MinValue;
            try
            {
                List<string> ordered = files.OrderBy(f => f, StringComparer.Ordinal).ToList();
                using (FileStream output = new FileStream(bundlePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
                {
                    using (System.IO.Compression.GZipStream gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
                    {
                        using (System.Formats.Tar.TarWriter writer = new System.Formats.Tar.TarWriter(gzip, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: false))
                        {
                            foreach (string file in ordered)
                            {
                                token.ThrowIfCancellationRequested();
                                DateTime written = File.GetLastWriteTimeUtc(file);
                                if (written > newest) newest = written;
                                string entryName = Path.GetRelativePath(full, file).Replace(Path.DirectorySeparatorChar, '/');
                                await writer.WriteEntryAsync(file, entryName, token).ConfigureAwait(false);
                                NoteProgress();
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDeleteFile(bundlePath);
                return ArchivePutResult.Failed(relative, "local_read_failed", "could not bundle " + full + ": " + ex.Message);
            }
            catch
            {
                TryDeleteFile(bundlePath);
                throw;
            }

            try
            {
                ArchivePutResult put = await pass.ArchiveBundleAsync(category, relative, bundlePath, newest == DateTime.MinValue ? DateTime.UtcNow : newest, full, files.Count, token).ConfigureAwait(false);
                NoteProgress();
                return put;
            }
            finally
            {
                TryDeleteFile(bundlePath);
            }
        }

        private void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                RecordError("temporary archive bundle could not be removed: " + path + ": " + ex.Message);
            }
        }

        // Frees an archived folder that also holds deny-listed files: every archived file and payload
        // folder goes, emptied folders go, and each deny-listed file stays where it is with its parents.
        private string? FreeHistoryFolderAround(string full, List<string> archived, List<string> payloadDirectories, List<string> denied)
        {
            if (!IsPathAllowed(full)) return "refusing to remove a symlink or a path outside the allowed roots";
            string? failure = OperatingSystem.IsWindows() ? null : MakeDirectoriesOwnerWritable(full);
            foreach (string file in archived)
            {
                string? fileFailure = FreeHistoryFile(file);
                if (fileFailure != null) return fileFailure;
            }

            foreach (string directory in payloadDirectories)
            {
                try
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
                catch (Exception ex)
                {
                    return ex.GetType().Name + ": " + ex.Message + (failure == null ? "" : "; could not make writable: " + failure);
                }
            }

            List<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(full, "*", RecursiveEnumeration()).OrderByDescending(d => d.Length).ToList();
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }

            foreach (string directory in directories)
            {
                try
                {
                    if (!IsSymlink(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
                }
                catch (Exception ex)
                {
                    return ex.GetType().Name + ": " + ex.Message;
                }
            }

            return null;
        }

        // Frees a history file whose verified copy is in the archive. Returns null when the file is gone,
        // otherwise the failure.
        private string? FreeHistoryFile(string path)
        {
            if (!IsPathAllowed(path)) return "refusing to remove a symlink or a path outside the allowed roots";
            try
            {
                if (OperatingSystem.IsWindows()) File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        // Frees a history folder whose files are all verified in the archive. A directory inside the
        // folder that lacks owner read, write or execute blocks removing its entries, so each one is given
        // those owner bits first. Only the folder and the directories inside it are changed; symlinks are
        // neither followed nor changed, and the operating system refuses the change on a directory the
        // admiral does not own. Returns null when the folder is gone, otherwise the failure, naming any
        // directory whose mode could not be changed.
        private string? FreeHistoryDirectory(string path)
        {
            if (!IsPathAllowed(path)) return "refusing to remove a symlink or a path outside the allowed roots";
            string? modeFailure = null;
            if (OperatingSystem.IsWindows())
            {
                foreach (string file in SafeEnumerateFilesRecursive(path, "*"))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch (Exception ex) { modeFailure ??= file + ": " + ex.Message; }
                }
            }
            else
            {
                modeFailure = MakeDirectoriesOwnerWritable(path);
            }

            try
            {
                Directory.Delete(path, true);
                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message + (modeFailure == null ? "" : "; could not make writable: " + modeFailure);
            }
        }

        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        private static string? MakeDirectoriesOwnerWritable(string root)
        {
            const UnixFileMode needed = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            string? firstFailure = null;
            int failures = 0;
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                try
                {
                    UnixFileMode mode = File.GetUnixFileMode(current);
                    if ((mode & needed) != needed) File.SetUnixFileMode(current, mode | needed);
                }
                catch (Exception ex)
                {
                    failures++;
                    firstFailure ??= current + ": " + ex.Message;
                }

                try
                {
                    foreach (string child in Directory.EnumerateDirectories(current))
                    {
                        if (IsSymlink(child)) continue;
                        pending.Push(child);
                    }
                }
                catch (Exception ex)
                {
                    failures++;
                    firstFailure ??= current + ": " + ex.Message;
                }
            }
            if (firstFailure == null) return null;
            return firstFailure + (failures > 1 ? " (and " + (failures - 1) + " more)" : "");
        }

        // Uploads the pass manifest and reports failed uploads as events, so a
        // broken archive reads as a failure and never as an idle pass.
        private async Task FinishArchivePassAsync(DiskLifecycleReport report, CancellationToken token)
        {
            HistoryArchivePass? pass = _ArchivePass;
            _ArchivePass = null;
            if (pass == null) return;

            report.ArchivedItems = pass.Uploaded;
            report.ArchiveReusedItems = pass.Reused;
            report.ArchivedBytes = pass.UploadedBytes;
            report.ArchiveFailures = pass.Failures.Count;
            report.ArchiveFreeFailures = pass.FreeFailures.Count;
            report.ArchiveDeferredItems = pass.Deferred;
            report.ArchiveCapped = pass.Deferred > 0;
            report.ArchiveTransactionCapReached = pass.CapReached;
            report.ArchiveSkippedForCap = pass.SkippedForCap;

            ArchivePutResult? manifest = null;
            try
            {
                manifest = await pass.WriteManifestAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                RecordError("archive manifest upload failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            string? manifestFailure = null;
            if (manifest != null)
            {
                if (manifest.Verified) report.ArchiveManifestKey = manifest.Key;
                else
                {
                    manifestFailure = "manifest " + manifest.Key + " not verified (" + manifest.FailureCode + ")";
                    RecordError("archive " + manifestFailure);
                }
            }

            if (pass.Failures.Count == 0 && manifestFailure == null && pass.FreeFailures.Count == 0 && !pass.CapReached) return;

            if (pass.CapReached)
            {
                // One cap event for the whole run, not one per file: the condition is
                // the store's, and the files are all kept locally until the cap is raised.
                string capSummary = "History archive: the object store download or transaction cap is exceeded in disk-lifecycle run " + pass.RunId
                    + "; the run stopped verifying after the first cap and kept " + pass.SkippedForCap + " history item" + (pass.SkippedForCap == 1 ? "" : "s")
                    + " locally. Raise the cap to resume archiving."
                    + (String.IsNullOrEmpty(pass.CapReason) ? "" : " " + pass.CapReason);
                await RecordArchiveEventAsync("disk_lifecycle.archive_cap_reached", capSummary, JsonSerializer.Serialize(new
                {
                    runId = pass.RunId,
                    skippedForCap = pass.SkippedForCap,
                    uploaded = pass.Uploaded,
                    reason = pass.CapReason
                }), token).ConfigureAwait(false);
            }

            if (pass.Failures.Count > 0 || manifestFailure != null)
            {
                List<string> lines = pass.Failures
                    .Take(_MaxReportedArchiveFailures)
                    .Select(f => f.Key + ": " + f.Value.FailureCode + (String.IsNullOrEmpty(f.Value.FailureMessage) ? "" : " (" + f.Value.FailureMessage + ")"))
                    .ToList();
                if (manifestFailure != null) lines.Add(manifestFailure);
                string summary = "History archive: " + pass.Failures.Count + " upload(s) failed verification in disk-lifecycle run " + pass.RunId
                    + "; each file is kept locally. " + String.Join("; ", lines)
                    + (pass.Failures.Count > _MaxReportedArchiveFailures ? "; and " + (pass.Failures.Count - _MaxReportedArchiveFailures) + " more" : "");
                await RecordArchiveEventAsync("disk_lifecycle.archive_failed", summary, JsonSerializer.Serialize(new
                {
                    runId = pass.RunId,
                    failures = pass.Failures.Count,
                    uploaded = pass.Uploaded,
                    manifestFailure = manifestFailure,
                    items = pass.Failures.Take(_MaxReportedArchiveFailures).Select(f => new { path = f.Key, key = f.Value.Key, code = f.Value.FailureCode, message = f.Value.FailureMessage })
                }), token).ConfigureAwait(false);
            }

            if (pass.FreeFailures.Count > 0)
            {
                List<string> lines = pass.FreeFailures
                    .Take(_MaxReportedArchiveFailures)
                    .Select(f => f.Key + ": " + f.Value)
                    .ToList();
                string summary = "History archive: " + pass.FreeFailures.Count + " archived item(s) could not be freed locally in disk-lifecycle run " + pass.RunId
                    + "; every file is verified in the archive, and the next pass retries the removal without uploading again. " + String.Join("; ", lines)
                    + (pass.FreeFailures.Count > _MaxReportedArchiveFailures ? "; and " + (pass.FreeFailures.Count - _MaxReportedArchiveFailures) + " more" : "");
                await RecordArchiveEventAsync("disk_lifecycle.archive_free_failed", summary, JsonSerializer.Serialize(new
                {
                    runId = pass.RunId,
                    freeFailures = pass.FreeFailures.Count,
                    items = pass.FreeFailures.Take(_MaxReportedArchiveFailures).Select(f => new { path = f.Key, message = f.Value })
                }), token).ConfigureAwait(false);
            }
        }

        private async Task RecordArchiveEventAsync(string eventType, string message, string payload, CancellationToken token)
        {
            try
            {
                await _Database.Events.CreateAsync(new ArmadaEvent
                {
                    EventType = eventType,
                    EntityType = "system",
                    Message = message,
                    Payload = payload
                }, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _Logging.Warn(_Header + "could not record " + eventType + " event: " + ex.Message);
            }
        }

        private void FinalizeReport(DiskLifecycleReport report)
        {
            foreach (DiskLifecycleCategory category in report.Categories)
            {
                report.TotalBytes += category.TotalBytes;
                report.TotalReclaimableBytes += category.ReclaimableBytes;
                report.ReclaimableItems += category.ReclaimableItems;
                report.ProtectedItems += category.ProtectedItems;
            }

            report.SkippedItems = report.Actions.Count(action => String.Equals(action.Disposition, "skipped", StringComparison.Ordinal));
        }

        private async Task EmitReportEventAsync(DiskLifecycleReport report, bool deleted, CancellationToken token)
        {
            try
            {
                Dictionary<string, object> perCategory = new Dictionary<string, object>();
                foreach (DiskLifecycleCategory category in report.Categories)
                {
                    perCategory[category.Category] = new
                    {
                        totalBytes = category.TotalBytes,
                        reclaimableBytes = category.ReclaimableBytes,
                        reclaimableItems = category.ReclaimableItems,
                        protectedItems = category.ProtectedItems
                    };
                }

                ArmadaEvent evt = new ArmadaEvent
                {
                    EventType = deleted ? "disk_lifecycle.reconcile" : "disk_lifecycle.scan",
                    EntityType = "system",
                    Message = (deleted
                        ? "Disk lifecycle reconciliation " + (report.DryRun ? "dry-run " : "") + "completed: " + report.ReclaimableItems + " reclaimable items, " + FormatBytes(report.TotalReclaimableBytes)
                        : "Disk lifecycle scan completed: " + report.ReclaimableItems + " reclaimable items, " + FormatBytes(report.TotalReclaimableBytes))
                        + (report.ArchiveCapped ? "; archive file bound reached, " + report.ArchiveDeferredItems + " history item" + (report.ArchiveDeferredItems == 1 ? "" : "s") + " deferred to the next pass" : "")
                        + (report.ArchiveTransactionCapReached ? "; archive stopped: object store cap exceeded, " + report.ArchiveSkippedForCap + " history item" + (report.ArchiveSkippedForCap == 1 ? "" : "s") + " kept locally" : "")
                        + (report.ErrorCount > 0 ? "; " + report.ErrorCount + " error" + (report.ErrorCount == 1 ? "" : "s") + ", first: " + report.Errors[0] : ""),
                    Payload = JsonSerializer.Serialize(new
                    {
                        dryRun = report.DryRun,
                        enabled = report.Enabled,
                        totalBytes = report.TotalBytes,
                        reclaimableBytes = report.TotalReclaimableBytes,
                        reclaimableItems = report.ReclaimableItems,
                        skippedItems = report.SkippedItems,
                        protectedItems = report.ProtectedItems,
                        errorCount = report.ErrorCount,
                        errors = report.Errors,
                        historyMode = report.HistoryMode,
                        archivedItems = report.ArchivedItems,
                        archiveReusedItems = report.ArchiveReusedItems,
                        archiveFailures = report.ArchiveFailures,
                        archiveFreeFailures = report.ArchiveFreeFailures,
                        archiveCapped = report.ArchiveCapped,
                        archiveDeferredItems = report.ArchiveDeferredItems,
                        archiveTransactionCapReached = report.ArchiveTransactionCapReached,
                        archiveSkippedForCap = report.ArchiveSkippedForCap,
                        categories = perCategory
                    })
                };
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not record disk-lifecycle event: " + ex.Message);
            }
        }

        private async Task EmitLowSpaceEventAsync(long freeBytes, int thresholdGb, CancellationToken token)
        {
            try
            {
                ArmadaEvent evt = new ArmadaEvent
                {
                    EventType = "disk_lifecycle.low_space",
                    EntityType = "system",
                    Message = "Low disk space: " + FormatBytes(freeBytes) + " free on the log volume, below the " + thresholdGb + " GB threshold",
                    Payload = JsonSerializer.Serialize(new
                    {
                        freeBytes = freeBytes,
                        thresholdGb = thresholdGb
                    })
                };
                await _Database.Events.CreateAsync(evt, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not record low-space event: " + ex.Message);
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }
            if (bytes < 1024 * 1024)
            {
                return String.Format("{0:0.##} KiB", bytes / 1024.0);
            }
            if (bytes < 1024L * 1024 * 1024)
            {
                return String.Format("{0:0.##} MiB", bytes / (1024.0 * 1024));
            }
            return String.Format("{0:0.##} GiB", bytes / (1024.0 * 1024 * 1024));
        }

        private static DiskLifecycleCategory NewCategory(string name)
        {
            return new DiskLifecycleCategory { Category = name };
        }

        private static bool IsTerminalMergeStatus(MergeStatusEnum status)
        {
            return MergeStatusRules.IsTerminal(status);
        }

        /// <summary>
        /// Record a failure of the running pass by name: logged, and carried on the report so an
        /// unreadable folder never reads as "nothing to reclaim".
        /// </summary>
        private void RecordError(string message)
        {
            _Logging.Warn(_Header + message);
            DiskLifecycleReport? report = _CurrentReport;
            if (report == null) return;
            report.ErrorCount++;
            if (report.Errors.Count < _MaxErrorRecords) report.Errors.Add(message);
        }

        private static DateTime SafeGetLastWriteTimeUtc(string path)
        {
            try
            {
                return Directory.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return DateTime.UtcNow;
            }
        }

        private static DateTime SafeGetFileLastWriteTimeUtc(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return DateTime.UtcNow;
            }
        }

        private static long SafeGetFileBytes(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private long SafeGetDirectoryBytes(string path)
        {
            long total = 0;
            try
            {
                foreach (FileInfo file in new DirectoryInfo(path).EnumerateFiles("*", RecursiveEnumeration()))
                {
                    // A file removed while it is measured holds no bytes.
                    try { total += file.Length; } catch (IOException) { }
                }
            }
            catch (DirectoryNotFoundException)
            {
                // A directory removed between listing and reading holds nothing to account for.
            }
            catch (Exception ex)
            {
                RecordError("cannot measure " + path + ": " + ex.GetType().Name + ": " + ex.Message);
            }
            return total;
        }

        // The newest write time of any file inside the directory. Directory times are not used, because
        // removing payload rewrites them; a directory with no files falls back to its own time. It stops at
        // the first file newer than the stop bound, because the caller only needs to know the tree is recent.
        private DateTime NewestWriteTimeUtc(string path, DateTime stopAfter)
        {
            DateTime newest = DateTime.MinValue;
            try
            {
                foreach (FileInfo file in new DirectoryInfo(path).EnumerateFiles("*", RecursiveEnumeration()))
                {
                    DateTime written = file.LastWriteTimeUtc;
                    if (written > newest) newest = written;
                    if (newest > stopAfter) return newest;
                }
            }
            catch (DirectoryNotFoundException)
            {
                // A directory removed between listing and reading is not recent.
            }
            catch (Exception ex)
            {
                RecordError("cannot read write times under " + path + ": " + ex.GetType().Name + ": " + ex.Message);
                return DateTime.UtcNow;
            }
            return newest == DateTime.MinValue ? SafeGetLastWriteTimeUtc(path) : newest;
        }

        // Outermost payload directories under an evidence folder; a payload directory's own contents are
        // not searched, and symlinks are never followed.
        private List<string> FindPayloadDirectories(string root)
        {
            List<string> result = new List<string>();
            Stack<string> pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                foreach (string child in SafeEnumerateDirectories(current))
                {
                    if (IsSymlink(child)) continue;
                    if (_EvidencePayloadDirectoryNames.Contains(Path.GetFileName(child))) result.Add(child);
                    else pending.Push(child);
                }
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static EnumerationOptions RecursiveEnumeration()
        {
            // Hidden entries such as .git count; symlinks are neither counted nor followed.
            return new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
        }

        private static long ReadFreeBytes(string path)
        {
            try
            {
                string probe = Directory.Exists(path) ? path : Path.GetTempPath();
                return new DriveInfo(Path.GetFullPath(probe)).AvailableFreeSpace;
            }
            catch
            {
                return -1;
            }
        }

        private List<string> SafeEnumerateDirectories(string root)
        {
            List<string> result = new List<string>();
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(root))
                {
                    result.Add(dir);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // A directory removed between listing and reading holds nothing to account for.
            }
            catch (Exception ex)
            {
                RecordError("cannot list directories in " + root + ": " + ex.GetType().Name + ": " + ex.Message);
            }
            return result;
        }

        private List<string> SafeEnumerateFiles(string root, string pattern)
        {
            List<string> result = new List<string>();
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, pattern))
                {
                    result.Add(file);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // A directory removed between listing and reading holds nothing to account for.
            }
            catch (Exception ex)
            {
                RecordError("cannot list files in " + root + ": " + ex.GetType().Name + ": " + ex.Message);
            }
            return result;
        }

        private List<string> SafeEnumerateFilesRecursive(string root, string pattern)
        {
            List<string> result = new List<string>();
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, String.IsNullOrWhiteSpace(pattern) ? "*" : pattern, RecursiveEnumeration()))
                {
                    result.Add(file);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // A directory removed between listing and reading holds nothing to account for.
            }
            catch (Exception ex)
            {
                RecordError("cannot list files under " + root + ": " + ex.GetType().Name + ": " + ex.Message);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private List<string> SafeEnumerateFileSystemEntries(string root)
        {
            List<string> result = new List<string>();
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(root))
                {
                    result.Add(entry);
                }
            }
            catch (DirectoryNotFoundException)
            {
                // A directory removed between listing and reading holds nothing to account for.
            }
            catch (Exception ex)
            {
                RecordError("cannot list entries in " + root + ": " + ex.GetType().Name + ": " + ex.Message);
            }
            return result;
        }

        #endregion

        #endregion

        #region Private-Classes

        /// <summary>
        /// One direct child of an extra retention root, measured once per pass.
        /// </summary>
        private sealed class DiskLifecycleRetentionItem
        {
            public string Path { get; set; } = String.Empty;

            public bool IsDirectory { get; set; } = false;

            public long Bytes { get; set; } = 0;

            public DateTime NewestWriteUtc { get; set; } = DateTime.MinValue;

            public bool Kept { get; set; } = false;
        }

        #endregion
    }
}
