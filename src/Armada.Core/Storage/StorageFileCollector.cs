namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using Armada.Core.Settings;

    /// <summary>
    /// Measures logical file lengths under a set of locations without reading file contents, deleting
    /// anything or following links. A scan is bounded by entry count, directory depth and elapsed time; when
    /// a bound is reached the report is partial and names the reason. Each invocation owns its state.
    /// </summary>
    public class StorageFileCollector
    {
        #region Private-Members

        private const int _MaxWarningsPerLocation = 10;

        #endregion

        #region Internal-Members

        /// <summary>
        /// Called with an entry's path immediately before its length is read. Tests use it to change the tree
        /// at the exact moment an entry is read; it is null in normal use.
        /// </summary>
        internal Action<string>? BeforeEntryRead { get; set; } = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Whether a path equals a root or lies below it, using the host's filesystem comparison rules.
        /// </summary>
        /// <param name="path">Full path to test.</param>
        /// <param name="root">Full root path.</param>
        /// <returns>True when the path is the root or below it.</returns>
        public static bool IsUnder(string path, string root)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (root == null) throw new ArgumentNullException(nameof(root));
            StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            string trimmed = Path.TrimEndingDirectorySeparator(root);
            return String.Equals(path, trimmed, comparison)
                || path.StartsWith(trimmed + Path.DirectorySeparatorChar, comparison);
        }

        /// <summary>
        /// Measure the locations. A location owns its subtree except the subtrees of other, more specific
        /// locations. Locations are scanned in the order given and share one entry and time budget.
        /// </summary>
        /// <param name="roots">Locations to measure; only <see cref="StorageLocation.Category"/> and <see cref="StorageLocation.Path"/> are read.</param>
        /// <param name="settings">Scan bounds.</param>
        /// <param name="token">Cancellation token; cancelling throws instead of returning a report.</param>
        /// <returns>The scan report.</returns>
        /// <exception cref="ArgumentNullException">Roots or settings are null.</exception>
        public StorageScanReport Collect(IEnumerable<StorageLocation> roots, StorageSettings settings, CancellationToken token = default)
        {
            if (roots == null) throw new ArgumentNullException(nameof(roots));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            StorageScanReport report = new StorageScanReport();
            Stopwatch watch = Stopwatch.StartNew();
            ScanBudget budget = new ScanBudget { MaxEntries = settings.MaxEntries, Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds), Watch = watch };

            List<StorageLocation> locations = NormalizeRoots(roots, report);
            foreach (StorageLocation location in locations)
            {
                token.ThrowIfCancellationRequested();
                List<string> ownedByOthers = locations
                    .Where(other => !Object.ReferenceEquals(other, location) && !PathsEqual(other.Path, location.Path) && IsUnder(other.Path, location.Path))
                    .Select(other => other.Path)
                    .ToList();
                if (budget.Reason != null)
                {
                    location.Status = StorageStatusEnum.Unavailable;
                    AddWarning(location, StorageWarningCodes.NotReached);
                    continue;
                }
                ScanLocation(location, ownedByOthers, settings, budget, token);
            }

            report.Locations = locations;
            report.EntriesExamined = budget.Examined;
            report.Volumes = ReadVolumes(locations);
            report.ElapsedMilliseconds = watch.ElapsedMilliseconds;
            report.MeasuredUtc = DateTime.UtcNow;

            foreach (StorageLocation location in locations)
            {
                foreach (string warning in location.Warnings) AddReason(report, warning);
            }
            if (budget.Reason != null) AddReason(report, budget.Reason);
            report.Status = report.PartialReasons.Count == 0 ? StorageStatusEnum.Complete : StorageStatusEnum.Partial;
            return report;
        }

        #endregion

        #region Private-Methods

        private static List<StorageLocation> NormalizeRoots(IEnumerable<StorageLocation> roots, StorageScanReport report)
        {
            List<StorageLocation> result = new List<StorageLocation>();
            StringComparer comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            HashSet<string> seen = new HashSet<string>(comparer);
            foreach (StorageLocation root in roots)
            {
                if (root == null || String.IsNullOrWhiteSpace(root.Path)) continue;
                try
                {
                    string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path));
                    if (!seen.Add(full)) continue;
                    result.Add(new StorageLocation { Category = root.Category, Path = full });
                }
                catch (ArgumentException)
                {
                    AddReason(report, StorageWarningCodes.InvalidPath);
                }
                catch (NotSupportedException)
                {
                    AddReason(report, StorageWarningCodes.InvalidPath);
                }
            }
            return result;
        }

        private void ScanLocation(StorageLocation location, List<string> ownedByOthers, StorageSettings settings, ScanBudget budget, CancellationToken token)
        {
            FileSystemInfo rootInfo;
            try
            {
                // A configured root may itself be a link; the operator chose it, so its target is measured.
                // Links found below the root are never followed.
                FileInfo asFile = new FileInfo(location.Path);
                if (asFile.Exists) rootInfo = asFile;
                else
                {
                    DirectoryInfo asDirectory = new DirectoryInfo(location.Path);
                    if (!asDirectory.Exists)
                    {
                        location.Status = StorageStatusEnum.Missing;
                        return;
                    }
                    rootInfo = asDirectory;
                }
            }
            catch (UnauthorizedAccessException)
            {
                location.Status = StorageStatusEnum.Unavailable;
                AddWarning(location, StorageWarningCodes.AccessDenied);
                return;
            }
            catch (IOException)
            {
                location.Status = StorageStatusEnum.Unavailable;
                AddWarning(location, StorageWarningCodes.Unreadable);
                return;
            }

            location.Status = StorageStatusEnum.Complete;
            location.Bytes = 0;

            if (rootInfo is FileInfo rootFile)
            {
                budget.Examined++;
                MeasureFile(location, rootFile, settings);
                return;
            }

            Stack<KeyValuePair<string, int>> pending = new Stack<KeyValuePair<string, int>>();
            pending.Push(new KeyValuePair<string, int>(location.Path, 0));
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                KeyValuePair<string, int> next = pending.Pop();
                IEnumerable<FileSystemInfo> children;
                try
                {
                    children = new DirectoryInfo(next.Key).EnumerateFileSystemInfos();
                    foreach (FileSystemInfo child in children)
                    {
                        if (budget.Reason != null) break;
                        token.ThrowIfCancellationRequested();
                        if (ownedByOthers.Any(other => IsUnder(child.FullName, other))) continue;
                        if (!budget.TryExamine())
                        {
                            Warn(location, budget.Reason!);
                            break;
                        }

                        if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            // A link can lead outside the root or back into it; its target is never measured.
                            location.LinksSkipped++;
                            continue;
                        }
                        if (child is DirectoryInfo)
                        {
                            if (next.Value + 1 > settings.MaxDepth) Warn(location, StorageWarningCodes.DepthLimit);
                            else pending.Push(new KeyValuePair<string, int>(child.FullName, next.Value + 1));
                            continue;
                        }
                        if (child is FileInfo file) MeasureFile(location, file, settings);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    Warn(location, StorageWarningCodes.AccessDenied);
                }
                catch (DirectoryNotFoundException)
                {
                    // The directory was removed while the scan was running; its files no longer exist.
                    location.VanishedEntries++;
                }
                catch (IOException)
                {
                    Warn(location, StorageWarningCodes.Unreadable);
                }
                if (budget.Reason != null) break;
            }
            if (budget.Reason != null) Warn(location, budget.Reason);
            TrimLargest(location, settings.LargestFileLimit);
        }

        private void MeasureFile(StorageLocation location, FileInfo file, StorageSettings settings)
        {
            try
            {
                BeforeEntryRead?.Invoke(file.FullName);
                file.Refresh();
                if (!file.Exists)
                {
                    location.VanishedEntries++;
                    return;
                }
                long size = file.Length;
                DateTime written = file.LastWriteTimeUtc;
                location.Bytes = (location.Bytes ?? 0) + size;
                location.FileCount++;
                if (!location.OldestWriteUtc.HasValue || written < location.OldestWriteUtc) location.OldestWriteUtc = written;
                if (!location.NewestWriteUtc.HasValue || written > location.NewestWriteUtc) location.NewestWriteUtc = written;
                location.LargestFiles.Add(new StorageFile { Path = file.FullName, Bytes = size, LastWriteUtc = written });
                if (location.LargestFiles.Count > settings.LargestFileLimit * 4) TrimLargest(location, settings.LargestFileLimit);
            }
            catch (UnauthorizedAccessException)
            {
                Warn(location, StorageWarningCodes.AccessDenied);
            }
            catch (FileNotFoundException)
            {
                location.VanishedEntries++;
            }
            catch (IOException)
            {
                Warn(location, StorageWarningCodes.Unreadable);
            }
        }

        private static void TrimLargest(StorageLocation location, int limit)
        {
            location.LargestFiles = location.LargestFiles.OrderByDescending(f => f.Bytes).Take(limit).ToList();
        }

        private static List<StorageVolume> ReadVolumes(List<StorageLocation> locations)
        {
            List<StorageVolume> volumes = new List<StorageVolume>();
            try
            {
                List<DriveInfo> drives = DriveInfo.GetDrives().OrderByDescending(d => d.RootDirectory.FullName.Length).ToList();
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (StorageLocation location in locations)
                {
                    if (location.Status == StorageStatusEnum.Missing || location.Status == StorageStatusEnum.Pending) continue;
                    DriveInfo? drive = drives.FirstOrDefault(d => IsUnder(location.Path, d.RootDirectory.FullName));
                    if (drive == null || !seen.Add(drive.RootDirectory.FullName)) continue;
                    StorageVolume volume = new StorageVolume { Path = drive.RootDirectory.FullName };
                    try
                    {
                        volume.TotalBytes = drive.TotalSize;
                        volume.AvailableBytes = drive.AvailableFreeSpace;
                    }
                    catch (IOException) { /* The volume stays listed without capacity. */ }
                    catch (UnauthorizedAccessException) { /* The volume stays listed without capacity. */ }
                    volumes.Add(volume);
                }
            }
            catch (IOException) { /* Volumes are best effort; the file figures stand without them. */ }
            catch (UnauthorizedAccessException) { /* Volumes are best effort; the file figures stand without them. */ }
            return volumes;
        }

        private static bool PathsEqual(string left, string right)
        {
            return IsUnder(left, right) && IsUnder(right, left);
        }

        private static void Warn(StorageLocation location, string code)
        {
            location.Status = StorageStatusEnum.Partial;
            AddWarning(location, code);
        }

        private static void AddWarning(StorageLocation location, string code)
        {
            if (location.Warnings.Count < _MaxWarningsPerLocation && !location.Warnings.Contains(code)) location.Warnings.Add(code);
        }

        private static void AddReason(StorageScanReport report, string? code)
        {
            if (code != null && !report.PartialReasons.Contains(code)) report.PartialReasons.Add(code);
        }

        #endregion

        private sealed class ScanBudget
        {
            public int MaxEntries { get; set; }
            public TimeSpan Timeout { get; set; }
            public Stopwatch Watch { get; set; } = new Stopwatch();
            public long Examined { get; set; }
            public string? Reason { get; set; }

            public bool TryExamine()
            {
                if (Watch.Elapsed >= Timeout)
                {
                    Reason = StorageWarningCodes.TimeLimit;
                    return false;
                }
                if (Examined >= MaxEntries)
                {
                    Reason = StorageWarningCodes.EntryLimit;
                    return false;
                }
                Examined++;
                return true;
            }
        }
    }
}
