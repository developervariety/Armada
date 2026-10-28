namespace Armada.Core.Storage
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Enums;
    using Armada.Core.Settings;

    /// <summary>
    /// Keeps a cached, read-only inventory of Armada's on-disk data and database. A background loop
    /// refreshes it on the configured interval and an administrator can request a refresh; a read returns
    /// the cache and never scans. Collection only measures: it deletes, moves and writes nothing except
    /// its own bounded history file.
    /// </summary>
    public class StorageInventoryService : IDisposable
    {
        #region Public-Members

        /// <summary>Category of the SQLite database file and its journal files.</summary>
        public const string CategoryDatabaseFiles = "DatabaseFiles";

        /// <summary>Category of the per-mission log directory.</summary>
        public const string CategoryMissionLogs = "MissionLogs";

        /// <summary>Category of the rest of the log directory.</summary>
        public const string CategoryLogs = "Logs";

        /// <summary>Category of the dock worktree directory.</summary>
        public const string CategoryDocks = "Docks";

        /// <summary>Category of the bare repository directory.</summary>
        public const string CategoryRepositories = "Repositories";

        /// <summary>Category of database and settings backup archives.</summary>
        public const string CategoryBackups = "Backups";

        /// <summary>Category of history-archive staging files.</summary>
        public const string CategoryArchiveStaging = "ArchiveStaging";

        /// <summary>Category of the code index.</summary>
        public const string CategoryCodeIndex = "CodeIndex";

        /// <summary>Category of operator evidence roots configured for captains.</summary>
        public const string CategoryOperatorEvidence = "OperatorEvidence";

        /// <summary>Category of everything else under the data directory.</summary>
        public const string CategoryOther = "Other";

        #endregion

        #region Private-Members

        private const int _PollSeconds = 10;

        private readonly string _Header = "[StorageInventoryService] ";
        private readonly ArmadaSettings _Settings;
        private readonly LoggingModule _Logging;
        private readonly StorageHistoryStore _History;
        private readonly DatabaseStorageMeasurer _Measurer;
        private readonly object _Sync = new object();
        private readonly CancellationTokenSource _Stop = new CancellationTokenSource();
        private StorageSnapshot _Snapshot = new StorageSnapshot();
        private List<StorageHistoryPoint> _HistoryPoints = new List<StorageHistoryPoint>();
        private bool _HistoryLoaded = false;
        private bool _HistoryReadable = true;
        private Task _Collection = Task.CompletedTask;
        private Task _Loop = Task.CompletedTask;
        private DateTime _LastStartedUtc = DateTime.MinValue;
        private bool _Disposed = false;

        #endregion

        #region Internal-Members

        /// <summary>
        /// Awaited at the start of every collection. Tests hold a collection open with it to observe
        /// request coalescing; it is null in normal use.
        /// </summary>
        internal Func<CancellationToken, Task>? BeforeCollect { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>Instantiate.</summary>
        /// <param name="settings">Live settings; the storage section is read on every cycle.</param>
        /// <param name="logging">Logging module.</param>
        public StorageInventoryService(ArmadaSettings settings, LoggingModule logging)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _History = new StorageHistoryStore(System.IO.Path.Combine(_Settings.DataDirectory, StorageHistoryStore.FileName));
            _Measurer = new DatabaseStorageMeasurer(_Settings.Database);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Start the background loop. It collects at start and then whenever the refresh interval has
        /// elapsed, while <see cref="StorageSettings.Enabled"/> is true. Calling it again does nothing.
        /// </summary>
        public void Start()
        {
            lock (_Sync)
            {
                if (_Disposed) throw new ObjectDisposedException(nameof(StorageInventoryService));
                if (!_Loop.IsCompleted) return;
                _Loop = Task.Run(LoopAsync);
            }
        }

        /// <summary>Return the cached inventory. This never scans.</summary>
        /// <returns>A copy of the cached snapshot.</returns>
        public StorageSnapshot Read()
        {
            lock (_Sync)
            {
                EnsureHistoryLoaded();
                StorageSnapshot result = Clone(_Snapshot);
                result.History = new List<StorageHistoryPoint>(_HistoryPoints);
                if (!_HistoryReadable) AddWarning(result, StorageWarningCodes.HistoryUnreadable);
                result.Collecting = !_Collection.IsCompleted;
                result.RefreshIntervalSeconds = _Settings.Storage.Enabled ? _Settings.Storage.RefreshIntervalSeconds : 0;
                result.HistoryCapacity = _Settings.Storage.HistoryCapacity;
                return result;
            }
        }

        /// <summary>
        /// Start a collection in the background unless one is already running. Concurrent requests coalesce
        /// into the running collection.
        /// </summary>
        /// <param name="exactCounts">When true the database collection counts every table's rows.</param>
        /// <returns>True when this call started a collection; false when one was already running or the service is disposed.</returns>
        public bool RequestRefresh(bool exactCounts = false)
        {
            lock (_Sync)
            {
                if (_Disposed || !_Collection.IsCompleted) return false;
                _LastStartedUtc = DateTime.UtcNow;
                _Collection = Task.Run(() => CollectAsync(exactCounts, _Stop.Token));
                return true;
            }
        }

        /// <summary>Run one collection to completion and cache its result.</summary>
        /// <param name="exactCounts">When true the database collection counts every table's rows.</param>
        /// <param name="token">Cancellation token; cancelling leaves the previous cache in place.</param>
        /// <returns>A task that completes when the cache holds the new figures or the failure code.</returns>
        public async Task CollectAsync(bool exactCounts, CancellationToken token = default)
        {
            if (BeforeCollect != null) await BeforeCollect(token).ConfigureAwait(false);
            try
            {
                using (CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    StorageSnapshot next = new StorageSnapshot();

                    StorageSettings storage = _Settings.Storage;
                    List<StorageLocation> roots = BuildRoots();
                    next.Files = await Task.Run(() => new StorageFileCollector().Collect(roots, storage, token), token).ConfigureAwait(false);

                    budget.CancelAfter(TimeSpan.FromSeconds(storage.TimeoutSeconds));
                    next.Database = await MeasureDatabaseAsync(exactCounts, budget.Token, token).ConfigureAwait(false);

                    next.MeasuredUtc = DateTime.UtcNow;
                    StorageHistoryPoint point = BuildPoint(next);
                    lock (_Sync)
                    {
                        EnsureHistoryLoaded();
                        _HistoryPoints = _History.Append(_HistoryPoints, point, storage.HistoryCapacity, out bool saved);
                        if (!saved)
                        {
                            AddWarning(next, StorageWarningCodes.HistoryNotSaved);
                            _Logging.Warn(_Header + "the storage history file could not be written");
                        }
                        _Snapshot = next;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The service is stopping or the caller cancelled; the cache keeps the previous figures.
            }
            catch (Exception ex)
            {
                // The previous figures stay cached; the failure is named in the snapshot and logged in full.
                _Logging.Warn(_Header + "storage collection failed: " + ex.GetType().Name + ": " + ex.Message);
                lock (_Sync) _Snapshot.LastError = StorageWarningCodes.CollectionFailed;
            }
        }

        /// <summary>Stop the background loop and any running collection.</summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Protected-Methods

        /// <summary>Dispose pattern.</summary>
        /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            Task collection;
            Task loop;
            lock (_Sync)
            {
                if (_Disposed) return;
                _Disposed = true;
                _Stop.Cancel();
                collection = _Collection;
                loop = _Loop;
            }
            try { Task.WhenAll(collection, loop).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { /* The loop and collection end by cancellation. */ }
            _Stop.Dispose();
        }

        #endregion

        #region Private-Methods

        private async Task LoopAsync()
        {
            while (!_Stop.IsCancellationRequested)
            {
                bool due;
                lock (_Sync)
                {
                    due = _Settings.Storage.Enabled
                        && DateTime.UtcNow - _LastStartedUtc >= TimeSpan.FromSeconds(_Settings.Storage.RefreshIntervalSeconds);
                }
                if (due) RequestRefresh();
                try { await Task.Delay(TimeSpan.FromSeconds(_PollSeconds), _Stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        // Callers hold _Sync.
        private void EnsureHistoryLoaded()
        {
            if (_HistoryLoaded) return;
            _HistoryPoints = _History.Load(out bool readable);
            _HistoryReadable = readable;
            _HistoryLoaded = true;
            if (!readable) _Logging.Warn(_Header + "the storage history file could not be read; history restarts empty");
        }

        private async Task<StorageDatabase> MeasureDatabaseAsync(bool exactCounts, CancellationToken budget, CancellationToken stop)
        {
            try
            {
                return await _Measurer.MeasureAsync(exactCounts, budget).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                return DatabaseFailure(_Settings.Database.Type, StorageStatusEnum.Partial, StorageWarningCodes.DatabaseTimedOut);
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException || ex is InvalidOperationException || ex is IOException || ex is TimeoutException)
            {
                // Provider messages can name the server or user; the log keeps the type and message, the response only the code.
                _Logging.Warn(_Header + "database statistics unavailable: " + ex.GetType().Name + ": " + ex.Message);
                return DatabaseFailure(_Settings.Database.Type, StorageStatusEnum.Unavailable, StorageWarningCodes.DatabaseUnavailable);
            }
        }

        private static StorageDatabase DatabaseFailure(DatabaseTypeEnum type, StorageStatusEnum status, string warning)
        {
            return new StorageDatabase
            {
                Provider = type.ToString(),
                MeasuredUtc = DateTime.UtcNow,
                Status = status,
                Warnings = new List<string> { warning }
            };
        }

        private List<StorageLocation> BuildRoots()
        {
            List<StorageLocation> roots = new List<StorageLocation>();
            if (_Settings.Database.Type == DatabaseTypeEnum.Sqlite)
            {
                string database = _Settings.Database.Filename;
                foreach (string suffix in new[] { "", "-wal", "-shm" })
                    roots.Add(new StorageLocation { Category = CategoryDatabaseFiles, Path = database + suffix });
            }
            string data = _Settings.DataDirectory;
            roots.Add(new StorageLocation { Category = CategoryBackups, Path = System.IO.Path.Combine(data, "backups") });
            roots.Add(new StorageLocation { Category = CategoryArchiveStaging, Path = System.IO.Path.Combine(data, "archive-staging") });
            roots.Add(new StorageLocation { Category = CategoryMissionLogs, Path = System.IO.Path.Combine(_Settings.LogDirectory, "missions") });
            roots.Add(new StorageLocation { Category = CategoryLogs, Path = _Settings.LogDirectory });
            roots.Add(new StorageLocation { Category = CategoryCodeIndex, Path = _Settings.CodeIndex.IndexDirectory });
            foreach (CaptainEvidenceRoot evidence in _Settings.CaptainEvidenceRoots)
            {
                if (evidence != null && !String.IsNullOrWhiteSpace(evidence.Path))
                    roots.Add(new StorageLocation { Category = CategoryOperatorEvidence, Path = evidence.Path });
            }
            roots.Add(new StorageLocation { Category = CategoryRepositories, Path = _Settings.ReposDirectory });
            roots.Add(new StorageLocation { Category = CategoryDocks, Path = _Settings.DocksDirectory });
            roots.Add(new StorageLocation { Category = CategoryOther, Path = data });
            return roots;
        }

        private static StorageHistoryPoint BuildPoint(StorageSnapshot snapshot)
        {
            StorageHistoryPoint point = new StorageHistoryPoint
            {
                MeasuredUtc = snapshot.MeasuredUtc ?? DateTime.UtcNow,
                DatabaseBytes = snapshot.Database.AllocatedBytes
            };
            if (snapshot.Files != null)
            {
                foreach (StorageLocation location in snapshot.Files.Locations.Where(l => l.Bytes.HasValue))
                    point.Categories[location.Category] = point.Categories.GetValueOrDefault(location.Category) + location.Bytes!.Value;
                point.ObservedFileBytes = point.Categories.Values.Sum();
            }
            return point;
        }

        private static void AddWarning(StorageSnapshot snapshot, string code)
        {
            if (!snapshot.Warnings.Contains(code)) snapshot.Warnings.Add(code);
        }

        private static T Clone<T>(T value)
        {
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
        }

        #endregion
    }
}
