namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Persisted, reference-counted leases for shared sibling worktrees. Multiple docks on one
    /// vessel resolve the same sibling path (for example <c>docks/&lt;Vessel&gt;/&lt;Sibling&gt;</c>), so a
    /// dock reclaiming its worktree must never remove a sibling another live dock, reviewer,
    /// rescue, landing check, or post-agent DoD still reads.
    /// <para>
    /// A lease is a JSON file under <c>&lt;LogDirectory&gt;/leases/&lt;vesselId&gt;</c> keyed by a
    /// hash of the sibling's absolute path, listing the dock ids holding it. Every mutation -
    /// acquire, release, remove-if-unleased, and reconciliation - is serialized per target
    /// through a lock file (<c>FileShare.None</c>), so none of them can interleave. Leases
    /// survive Admiral restarts by design; a crash leaves a stale lease that
    /// <see cref="ReconcileAsync"/> purges once the holder dock is no longer active.
    /// </summary>
    public class SiblingLeaseRegistry
    {
        #region Private-Members

        private string _Header = "[SiblingLeaseRegistry] ";
        private LoggingModule _Logging;
        private DatabaseDriver _Database;
        private ArmadaSettings _Settings;
        private TimeSpan _LockTimeout;
        private TimeSpan _LockRetryInterval;

        private static readonly TimeSpan _DefaultLockTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan _DefaultLockRetryInterval = TimeSpan.FromMilliseconds(200);

        private static readonly JsonSerializerOptions _JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module.</param>
        /// <param name="database">Database driver used to verify holder-dock liveness.</param>
        /// <param name="settings">Application settings (lease root derives from the log directory).</param>
        /// <param name="lockTimeout">How long an operation waits for a contended lease lock before it
        /// gives up; null uses the default of ten seconds.</param>
        public SiblingLeaseRegistry(LoggingModule logging, DatabaseDriver database, ArmadaSettings settings, TimeSpan? lockTimeout = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _LockTimeout = lockTimeout.HasValue && lockTimeout.Value > TimeSpan.Zero ? lockTimeout.Value : _DefaultLockTimeout;
            _LockRetryInterval = _LockTimeout < _DefaultLockRetryInterval ? _LockTimeout : _DefaultLockRetryInterval;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Take a lease on a shared sibling worktree for a dock. Idempotent for the same dock.
        /// </summary>
        /// <param name="dockId">Dock identifier holding the lease.</param>
        /// <param name="vesselId">Vessel identifier.</param>
        /// <param name="siblingAbsolutePath">Absolute path of the shared sibling worktree.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the lease is held by this dock after the call.</returns>
        public async Task<bool> TryAcquireAsync(string dockId, string vesselId, string siblingAbsolutePath, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(dockId)) throw new ArgumentNullException(nameof(dockId));
            if (String.IsNullOrWhiteSpace(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            if (String.IsNullOrWhiteSpace(siblingAbsolutePath)) throw new ArgumentNullException(nameof(siblingAbsolutePath));

            string leasePath = GetLeasePath(vesselId, siblingAbsolutePath);
            using FileStream? leaseLock = await AcquireLockAsync(leasePath, token).ConfigureAwait(false);

            LeaseDocument? doc = await ReadLeaseAsync(leasePath, token).ConfigureAwait(false);
            doc ??= new LeaseDocument { VesselId = vesselId, SiblingPath = Path.GetFullPath(siblingAbsolutePath) };

            if (!doc.DockIds.Contains(dockId))
            {
                doc.DockIds.Add(dockId);
                doc.UpdatedUtc = DateTime.UtcNow;
                await WriteLeaseAsync(leasePath, doc, token).ConfigureAwait(false);
                _Logging.Debug(_Header + "dock " + dockId + " leased sibling " + siblingAbsolutePath + " (holders: " + doc.DockIds.Count + ")");
            }
            return true;
        }

        /// <summary>
        /// Release this dock's lease on a shared sibling worktree. Deletes the lease file when
        /// no holder remains. Never removes the sibling itself; use
        /// <see cref="RemoveIfUnleasedAsync"/> for removal.
        /// </summary>
        /// <param name="dockId">Dock identifier releasing the lease.</param>
        /// <param name="vesselId">Vessel identifier.</param>
        /// <param name="siblingAbsolutePath">Absolute path of the shared sibling worktree.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when no holder remains after the release.</returns>
        public async Task<bool> ReleaseAsync(string dockId, string vesselId, string siblingAbsolutePath, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(dockId)) throw new ArgumentNullException(nameof(dockId));
            if (String.IsNullOrWhiteSpace(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            if (String.IsNullOrWhiteSpace(siblingAbsolutePath)) throw new ArgumentNullException(nameof(siblingAbsolutePath));

            string leasePath = GetLeasePath(vesselId, siblingAbsolutePath);
            using FileStream? leaseLock = await AcquireLockAsync(leasePath, token).ConfigureAwait(false);

            LeaseDocument? doc = await ReadLeaseAsync(leasePath, token).ConfigureAwait(false);
            if (doc == null)
            {
                return true;
            }

            doc.DockIds.RemoveAll(id => String.Equals(id, dockId, StringComparison.Ordinal));
            if (doc.DockIds.Count == 0)
            {
                DeleteLeaseFile(leasePath);
                _Logging.Debug(_Header + "dock " + dockId + " released last lease on sibling " + siblingAbsolutePath);
                return true;
            }

            doc.UpdatedUtc = DateTime.UtcNow;
            await WriteLeaseAsync(leasePath, doc, token).ConfigureAwait(false);
            _Logging.Debug(_Header + "dock " + dockId + " released lease on sibling " + siblingAbsolutePath + " (holders remain: " + doc.DockIds.Count + ")");
            return false;
        }

        /// <summary>
        /// Whether any dock other than <paramref name="excludeDockId"/> holds a lease on the
        /// sibling target.
        /// </summary>
        /// <param name="vesselId">Vessel identifier.</param>
        /// <param name="siblingAbsolutePath">Absolute path of the shared sibling worktree.</param>
        /// <param name="excludeDockId">Dock identifier to ignore, or null to count every holder.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when another dock holds a lease.</returns>
        public async Task<bool> HasOtherLeaseAsync(string vesselId, string siblingAbsolutePath, string? excludeDockId, CancellationToken token = default)
        {
            string leasePath = GetLeasePath(vesselId, siblingAbsolutePath);
            LeaseDocument? doc = await ReadLeaseAsync(leasePath, token).ConfigureAwait(false);
            if (doc == null)
            {
                return false;
            }

            foreach (string holder in doc.DockIds)
            {
                if (String.IsNullOrWhiteSpace(excludeDockId) || !String.Equals(holder, excludeDockId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Release this dock's lease on the sibling target and, when no other dock holds a
        /// lease, execute <paramref name="removeAction"/> while the target lock is still held.
        /// Holding the lock across the removal serializes it against a concurrent acquire, so a
        /// provisioning dock can never have its sibling removed mid-provision.
        /// </summary>
        /// <param name="dockId">Dock identifier.</param>
        /// <param name="vesselId">Vessel identifier.</param>
        /// <param name="siblingAbsolutePath">Absolute path of the shared sibling worktree.</param>
        /// <param name="removeAction">Action that removes the sibling worktree.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the sibling was removed.</returns>
        public async Task<bool> RemoveIfUnleasedAsync(string dockId, string vesselId, string siblingAbsolutePath, Func<CancellationToken, Task> removeAction, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(dockId)) throw new ArgumentNullException(nameof(dockId));
            if (String.IsNullOrWhiteSpace(vesselId)) throw new ArgumentNullException(nameof(vesselId));
            if (String.IsNullOrWhiteSpace(siblingAbsolutePath)) throw new ArgumentNullException(nameof(siblingAbsolutePath));
            if (removeAction == null) throw new ArgumentNullException(nameof(removeAction));

            string leasePath = GetLeasePath(vesselId, siblingAbsolutePath);
            using FileStream? leaseLock = await AcquireLockAsync(leasePath, token).ConfigureAwait(false);

            LeaseDocument? doc = await ReadLeaseAsync(leasePath, token).ConfigureAwait(false);
            if (doc != null)
            {
                doc.DockIds.RemoveAll(id => String.Equals(id, dockId, StringComparison.Ordinal));

                // A holder whose dock is already reclaimed can never need the sibling again. Its
                // lease is a leftover, so it must not keep the sibling on disk.
                List<string> liveHolders = new List<string>();
                foreach (string holder in doc.DockIds)
                {
                    HolderStateEnum state = await GetHolderStateAsync(holder, token).ConfigureAwait(false);
                    if (state == HolderStateEnum.Inactive)
                    {
                        _Logging.Info(_Header + "ignoring lease on sibling " + siblingAbsolutePath + " held by reclaimed dock " + holder);
                        continue;
                    }
                    liveHolders.Add(holder);
                }
                doc.DockIds = liveHolders;

                if (doc.DockIds.Count > 0)
                {
                    doc.UpdatedUtc = DateTime.UtcNow;
                    await WriteLeaseAsync(leasePath, doc, token).ConfigureAwait(false);
                    _Logging.Info(_Header + "keeping shared sibling " + siblingAbsolutePath + ": still leased by " + String.Join(", ", doc.DockIds));
                    return false;
                }
                DeleteLeaseFile(leasePath);
            }

            await removeAction(token).ConfigureAwait(false);
            _Logging.Info(_Header + "removed shared sibling " + siblingAbsolutePath + " for dock " + dockId + " (no remaining leases)");
            return true;
        }

        /// <summary>
        /// Purge lease files whose holder docks are no longer active, and delete lease files
        /// that hold no dock ids. Called at startup and periodically so a crashed Admiral does
        /// not pin a sibling forever. Each lease is reconciled under the same per-target lock
        /// that acquire and release take, so reconciliation never drops a holder another
        /// operation adds while it runs; a lease whose lock stays contended is skipped until the
        /// next pass.
        /// </summary>
        /// <param name="grace">Minimum age of the lease record before a holder whose dock record
        /// no longer exists is purged. A holder whose dock exists but is inactive is purged
        /// immediately regardless of age.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of lease files removed.</returns>
        public async Task<int> ReconcileAsync(TimeSpan grace, CancellationToken token = default)
        {
            int removedFiles = 0;
            string leaseRoot = Path.Combine(_Settings.LogDirectory, "leases");
            if (!Directory.Exists(leaseRoot))
            {
                return 0;
            }

            foreach (string vesselDir in SafeEnumerateDirectories(leaseRoot))
            {
                foreach (string leasePath in SafeEnumerateFiles(vesselDir, "*.lease"))
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        using FileStream? leaseLock = await AcquireLockAsync(leasePath, token).ConfigureAwait(false);

                        if (!File.Exists(leasePath))
                        {
                            continue;
                        }

                        LeaseDocument? doc = await ReadLeaseAsync(leasePath, token).ConfigureAwait(false);
                        if (doc == null)
                        {
                            DeleteLeaseFile(leasePath);
                            removedFiles++;
                            continue;
                        }

                        bool leaseOlderThanGrace = DateTime.UtcNow - doc.UpdatedUtc >= grace;
                        bool changed = false;
                        List<string> remaining = new List<string>();
                        foreach (string holder in doc.DockIds)
                        {
                            HolderStateEnum state = await GetHolderStateAsync(holder, token).ConfigureAwait(false);
                            bool purge = state == HolderStateEnum.Inactive
                                || (state == HolderStateEnum.Missing && leaseOlderThanGrace);
                            if (purge)
                            {
                                changed = true;
                            }
                            else
                            {
                                remaining.Add(holder);
                            }
                        }

                        if (remaining.Count == 0)
                        {
                            DeleteLeaseFile(leasePath);
                            removedFiles++;
                            _Logging.Info(_Header + "purged stale sibling lease " + leasePath + " (no active holder docks)");
                        }
                        else if (changed)
                        {
                            doc.DockIds = remaining;
                            doc.UpdatedUtc = DateTime.UtcNow;
                            await WriteLeaseAsync(leasePath, doc, token).ConfigureAwait(false);
                            _Logging.Info(_Header + "trimmed stale holders from sibling lease " + leasePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _Logging.Warn(_Header + "lease reconciliation failed for " + leasePath + ": " + ex.Message);
                    }
                }
            }

            return removedFiles;
        }

        /// <summary>
        /// Absolute lease file path for a sibling target.
        /// </summary>
        /// <param name="vesselId">Vessel identifier.</param>
        /// <param name="siblingAbsolutePath">Absolute path of the shared sibling worktree.</param>
        /// <returns>Lease file path.</returns>
        public string GetLeasePath(string vesselId, string siblingAbsolutePath)
        {
            string key = vesselId + "|" + Path.GetFullPath(siblingAbsolutePath);
            using SHA256 sha = SHA256.Create();
            byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
            StringBuilder sb = new StringBuilder();
            foreach (byte b in hashBytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return Path.Combine(_Settings.LogDirectory, "leases", vesselId, sb.ToString() + ".lease");
        }

        #endregion

        #region Private-Methods

        private async Task<FileStream?> AcquireLockAsync(string leasePath, CancellationToken token)
        {
            string lockPath = leasePath + ".lock";
            string? directory = Path.GetDirectoryName(lockPath);
            if (!String.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            DateTime deadline = DateTime.UtcNow + _LockTimeout;
            while (true)
            {
                try
                {
                    return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    if (DateTime.UtcNow >= deadline) break;
                    await Task.Delay(_LockRetryInterval, token).ConfigureAwait(false);
                }
            }

            throw new IOException("could not acquire sibling lease lock " + lockPath + " (contended for too long)");
        }

        private async Task<LeaseDocument?> ReadLeaseAsync(string leasePath, CancellationToken token)
        {
            if (!File.Exists(leasePath))
            {
                return null;
            }

            try
            {
                string json = await File.ReadAllTextAsync(leasePath, token).ConfigureAwait(false);
                LeaseDocument? doc = JsonSerializer.Deserialize<LeaseDocument>(json, _JsonOptions);
                if (doc != null && doc.DockIds == null)
                {
                    doc.DockIds = new List<string>();
                }
                return doc;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not parse lease " + leasePath + ": " + ex.Message);
                return null;
            }
        }

        private async Task WriteLeaseAsync(string leasePath, LeaseDocument doc, CancellationToken token)
        {
            string? directory = Path.GetDirectoryName(leasePath);
            if (!String.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            string json = JsonSerializer.Serialize(doc, _JsonOptions);
            await File.WriteAllTextAsync(leasePath, json, token).ConfigureAwait(false);
        }

        private void DeleteLeaseFile(string leasePath)
        {
            try
            {
                if (File.Exists(leasePath))
                {
                    File.Delete(leasePath);
                }
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not delete lease " + leasePath + ": " + ex.Message);
            }
        }

        private async Task<HolderStateEnum> GetHolderStateAsync(string dockId, CancellationToken token)
        {
            try
            {
                Dock? dock = await _Database.Docks.ReadAsync(dockId, token).ConfigureAwait(false);
                if (dock == null) return HolderStateEnum.Missing;
                return dock.Active ? HolderStateEnum.Active : HolderStateEnum.Inactive;
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "could not read dock " + dockId + " during lease reconciliation: " + ex.Message);
                return HolderStateEnum.Active;
            }
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

        /// <summary>
        /// What reconciliation knows about a lease holder's dock record.
        /// </summary>
        private enum HolderStateEnum
        {
            /// <summary>The dock exists and is active, or its record could not be read.</summary>
            Active,

            /// <summary>The dock exists and is inactive.</summary>
            Inactive,

            /// <summary>No dock record exists for the holder.</summary>
            Missing
        }

        /// <summary>
        /// Lease document persisted to disk.
        /// </summary>
        private sealed class LeaseDocument
        {
            public string VesselId { get; set; } = String.Empty;

            public string SiblingPath { get; set; } = String.Empty;

            public List<string> DockIds { get; set; } = new List<string>();

            public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        }

        #endregion
    }
}
