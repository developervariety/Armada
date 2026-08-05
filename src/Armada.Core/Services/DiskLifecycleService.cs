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
    /// </summary>
    public class DiskLifecycleService
    {
        #region Public-Members

        #endregion

        #region Private-Members

        private string _Header = "[DiskLifecycleService] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private SiblingLeaseRegistry _Leases;

        private const int _MaxActionRecords = 2000;
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

        private static readonly string[] _TempArtifactPrefixes = new string[]
        {
            "armada-chk-",
            "armada_test_",
            "armada-code-index-",
            "armada-model-validation-",
            "armada_init_",
            "armada-iso-",
            "armada-backup-",
            "armada-restore-"
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
            DiskLifecycleSettings section = _Settings.DiskLifecycle;
            bool delete = deleteWhenEnabled && section.Enabled && !section.DryRun;
            DiskLifecycleReport report = new DiskLifecycleReport
            {
                Enabled = section.Enabled,
                DryRun = !delete,
                ScannedUtc = DateTime.UtcNow
            };

            try
            {
                await ReconcileLeasesAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "sibling lease reconciliation failed: " + ex.Message);
            }

            try
            {
                await ScanOrphanDocks(section, delete, report, token).ConfigureAwait(false);
                await ScanIntegrationWorktrees(section, delete, report, token).ConfigureAwait(false);
                await ScanMergeQueueWorktrees(section, delete, report, token).ConfigureAwait(false);
                await ScanMissionLogs(section, delete, report, token).ConfigureAwait(false);
                await ScanDiffSnapshots(section, delete, report, token).ConfigureAwait(false);
                await ScanInstructionSnapshots(section, delete, report, token).ConfigureAwait(false);
                await ScanDockMetadata(section, delete, report, token).ConfigureAwait(false);
                await ScanTempArtifacts(section, delete, report, token).ConfigureAwait(false);
                await ScanBackups(section, delete, report, token).ConfigureAwait(false);
                ScanBareRepos(report);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "disk lifecycle scan failed: " + ex.Message);
            }

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
                    // directory (docks/<Vessel>/<dockId>/../EcuLink -> docks/<Vessel>/EcuLink), so
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
                    if (dock.Active && !String.IsNullOrWhiteSpace(dock.WorktreePath))
                    {
                        protectedPaths.Add(Path.GetFullPath(dock.WorktreePath!));
                    }
                }

                foreach (MissionStatusEnum status in new MissionStatusEnum[]
                {
                    MissionStatusEnum.Pending,
                    MissionStatusEnum.Assigned,
                    MissionStatusEnum.InProgress
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
                        if (dock != null && !String.IsNullOrWhiteSpace(dock.WorktreePath))
                        {
                            protectedPaths.Add(Path.GetFullPath(dock.WorktreePath!));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not enumerate protected docks for orphan-dock scan: " + ex.Message);
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

                    if (siblingPaths.Contains(full))
                    {
                        RecordAction(report, category, full, "protected", "shared sibling worktree");
                        category.ProtectedItems++;
                        continue;
                    }

                    if (protectedPaths.Contains(full))
                    {
                        RecordAction(report, category, full, "protected", "active dock or preserved for a live mission");
                        category.ProtectedItems++;
                        continue;
                    }

                    if (!LooksLikeWorktree(full))
                    {
                        category.ProtectedItems++;
                        continue;
                    }

                    DateTime lastWrite = SafeGetLastWriteTimeUtc(full);
                    if (lastWrite > cutoff)
                    {
                        category.ProtectedItems++;
                        continue;
                    }

                    category.ReclaimableBytes += bytes;
                    category.ReclaimableItems++;

                    if (delete)
                    {
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
            ScanWorktreeDirs(root, retention, delete, report, category, token);
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
            ScanExpiredFiles(root, "*.log", retention, delete, report, category, token);
            report.Categories.Add(category);
        }

        private async Task ScanDiffSnapshots(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryDiffs);
            string root = Path.Combine(_Settings.LogDirectory, "diffs");
            TimeSpan retention = TimeSpan.FromDays(Math.Max(0, section.DiffSnapshotRetentionDays));
            ScanExpiredFiles(root, "*.diff", retention, delete, report, category, token);
            report.Categories.Add(category);
        }

        private async Task ScanInstructionSnapshots(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryInstructions);
            string root = Path.Combine(_Settings.LogDirectory, "instructions");
            TimeSpan retention = TimeSpan.FromDays(Math.Max(0, section.InstructionRetentionDays));
            ScanExpiredFiles(root, "*", retention, delete, report, category, token);
            report.Categories.Add(category);
        }

        private async Task ScanDockMetadata(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryDockMetadata);
            string root = Path.Combine(_Settings.LogDirectory, "docks");
            TimeSpan retention = TimeSpan.FromDays(Math.Max(0, section.DockMetadataRetentionDays));
            ScanExpiredFiles(root, "*.start", retention, delete, report, category, token);
            report.Categories.Add(category);
        }

        private async Task ScanTempArtifacts(DiskLifecycleSettings section, bool delete, DiskLifecycleReport report, CancellationToken token)
        {
            DiskLifecycleCategory category = NewCategory(_CategoryTempArtifacts);
            string root = Path.GetTempPath();
            TimeSpan retention = TimeSpan.FromHours(Math.Max(1, section.TempArtifactRetentionHours));
            DateTime cutoff = DateTime.UtcNow.Subtract(retention);

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

        private async Task ScanExpiredFiles(string root, string pattern, TimeSpan retention, bool delete, DiskLifecycleReport report, DiskLifecycleCategory category, CancellationToken token)
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

        #endregion

        #region Guards and Helpers

        private bool IsPathAllowed(string fullPath)
        {
            if (IsSymlink(fullPath))
            {
                return false;
            }

            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
            foreach (string root in ResolveAllowedRoots())
            {
                // Trim trailing separators: Path.GetTempPath() returns one on macOS/Linux and a
                // naive concatenation would produce a double separator that never matches.
                string rootNorm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                if (String.Equals(normalized, rootNorm, StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(rootNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
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
                if (OperatingSystem.IsWindows())
                {
                    foreach (string file in SafeEnumerateFiles(path, "*"))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    }
                }
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
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using Process process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return -1;
            }

            Task<string> drainStdout = process.StandardOutput.ReadToEndAsync();
            Task<string> drainStderr = process.StandardError.ReadToEndAsync();

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                throw;
            }

            await drainStdout.ConfigureAwait(false);
            await drainStderr.ConfigureAwait(false);
            return process.ExitCode;
        }

        private void RecordAction(DiskLifecycleReport report, DiskLifecycleCategory category, string path, string disposition, string reason)
        {
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
                    Message = deleted
                        ? "Disk lifecycle reconciliation " + (report.DryRun ? "dry-run " : "") + "completed: " + report.ReclaimableItems + " reclaimable items, " + FormatBytes(report.TotalReclaimableBytes)
                        : "Disk lifecycle scan completed: " + report.ReclaimableItems + " reclaimable items, " + FormatBytes(report.TotalReclaimableBytes),
                    Payload = JsonSerializer.Serialize(new
                    {
                        dryRun = report.DryRun,
                        enabled = report.Enabled,
                        totalBytes = report.TotalBytes,
                        reclaimableBytes = report.TotalReclaimableBytes,
                        reclaimableItems = report.ReclaimableItems,
                        skippedItems = report.SkippedItems,
                        protectedItems = report.ProtectedItems,
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
            return status == MergeStatusEnum.Landed
                || status == MergeStatusEnum.Failed
                || status == MergeStatusEnum.Cancelled;
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

        private static long SafeGetDirectoryBytes(string path)
        {
            long total = 0;
            foreach (string file in SafeEnumerateFiles(path, "*"))
            {
                total += SafeGetFileBytes(file);
            }
            return total;
        }

        private static List<string> SafeEnumerateDirectories(string root)
        {
            List<string> result = new List<string>();
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(root))
                {
                    result.Add(dir);
                }
            }
            catch (Exception)
            {
            }
            return result;
        }

        private static List<string> SafeEnumerateFiles(string root, string pattern)
        {
            List<string> result = new List<string>();
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, pattern))
                {
                    result.Add(file);
                }
            }
            catch (Exception)
            {
            }
            return result;
        }

        private static List<string> SafeEnumerateFileSystemEntries(string root)
        {
            List<string> result = new List<string>();
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(root))
                {
                    result.Add(entry);
                }
            }
            catch (Exception)
            {
            }
            return result;
        }

        #endregion

        #endregion
    }
}
