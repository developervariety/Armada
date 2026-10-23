namespace Armada.Core.Database.Sqlite
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.Common;
    using System.IO;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Data.Sqlite;
    using SQLitePCL;

    /// <summary>
    /// Provides a FIFO write gate for each open SQLite database. Other database providers do not need this gate.
    /// </summary>
    internal static class SqliteWriteGate
    {
        #region Private-Members

        private const int _PruneEveryLookups = 64;
        private const int _PruneMinimumEntries = 256;
        private static readonly object _RegistryLock = new object();
        private static readonly Dictionary<string, WeakReference<Gate>> _FileGates = new Dictionary<string, WeakReference<Gate>>(FilePathComparer());
        private static readonly Dictionary<string, WeakReference<Gate>> _SharedMemoryGates = new Dictionary<string, WeakReference<Gate>>(StringComparer.Ordinal);
        private static readonly ConditionalWeakTable<sqlite3, Gate> _PrivateMemoryGates = new ConditionalWeakTable<sqlite3, Gate>();
        private static readonly AsyncLocal<Lease?> _CurrentLease = new AsyncLocal<Lease?>();
        private static long _RegistryLookups;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Enter the write gate for an open SQLite connection. A non-SQLite connection returns no lease.
        /// </summary>
        /// <param name="openConnection">An open database connection.</param>
        /// <param name="token">Cancellation token for a queued request.</param>
        /// <returns>A lease, or null for another database provider.</returns>
        internal static Task<IDisposable?> EnterAsync(DbConnection openConnection, CancellationToken token = default)
        {
            if (openConnection == null) throw new ArgumentNullException(nameof(openConnection));
            if (openConnection is not SqliteConnection sqliteConnection) return Task.FromResult<IDisposable?>(null);
            if (sqliteConnection.State != ConnectionState.Open) throw new InvalidOperationException("The SQLite connection must be open before entering its write gate.");

            token.ThrowIfCancellationRequested();
            string? sharedMemoryKey = SharedMemoryKey(sqliteConnection);
            Gate gate;
            if (sharedMemoryKey != null)
            {
                gate = GetRegisteredGate(_SharedMemoryGates, "memory-shared:" + sharedMemoryKey);
            }
            else if (IsPrivateMemory(sqliteConnection))
            {
                sqlite3? handle = sqliteConnection.Handle;
                if (handle == null) throw new InvalidOperationException("The open SQLite connection has no native database handle.");
                gate = _PrivateMemoryGates.GetValue(handle, static _ => new Gate("memory-private"));
            }
            else
            {
                string key = FileKey(sqliteConnection.DataSource);
                gate = GetRegisteredGate(_FileGates, key);
            }

            return gate.Enter(token);
        }

        #endregion

        #region Private-Methods

        private static Gate GetRegisteredGate(Dictionary<string, WeakReference<Gate>> registry, string key)
        {
            lock (_RegistryLock)
            {
                long lookups = ++_RegistryLookups;
                if (lookups % _PruneEveryLookups == 0)
                {
                    if (_FileGates.Count > _PruneMinimumEntries) Prune(_FileGates);
                    if (_SharedMemoryGates.Count > _PruneMinimumEntries) Prune(_SharedMemoryGates);
                }

                if (registry.TryGetValue(key, out WeakReference<Gate>? weakGate)
                    && weakGate.TryGetTarget(out Gate? existing))
                {
                    return existing;
                }

                Gate created = new Gate(key);
                registry[key] = new WeakReference<Gate>(created);
                return created;
            }
        }

        private static void Prune(Dictionary<string, WeakReference<Gate>> registry)
        {
            List<string> expired = new List<string>();
            foreach (KeyValuePair<string, WeakReference<Gate>> entry in registry)
            {
                if (!entry.Value.TryGetTarget(out Gate? _)) expired.Add(entry.Key);
            }

            foreach (string key in expired) registry.Remove(key);
        }

        private static StringComparer FilePathComparer()
        {
            return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }

        private static bool IsPrivateMemory(SqliteConnection connection)
        {
            SqliteConnectionStringBuilder builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
            string dataSource = builder.DataSource ?? String.Empty;
            bool uriMemory = false;
            bool uriShared = false;
            string? uriName = null;
            ReadUriOptions(dataSource, out uriName, out uriMemory, out uriShared);

            if (!dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                && (String.IsNullOrEmpty(dataSource) || String.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)))
                return true;

            bool memory = builder.Mode == SqliteOpenMode.Memory || uriMemory
                || String.IsNullOrEmpty(dataSource)
                || String.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase);
            bool shared = builder.Cache == SqliteCacheMode.Shared || uriShared;
            return memory && !shared;
        }

        private static string? SharedMemoryKey(SqliteConnection connection)
        {
            SqliteConnectionStringBuilder builder = new SqliteConnectionStringBuilder(connection.ConnectionString);
            string dataSource = builder.DataSource ?? String.Empty;
            ReadUriOptions(dataSource, out string? uriName, out bool uriMemory, out bool uriShared);

            bool memory = builder.Mode == SqliteOpenMode.Memory || uriMemory
                || String.Equals(uriName, ":memory:", StringComparison.OrdinalIgnoreCase);
            bool shared = builder.Cache == SqliteCacheMode.Shared || uriShared;
            if (!memory || !shared) return null;

            string name = uriName ?? dataSource;
            if (String.IsNullOrWhiteSpace(name)) return null;
            if (String.Equals(name, ":memory:", StringComparison.OrdinalIgnoreCase)
                && !dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
            return name;
        }

        private static void ReadUriOptions(string dataSource, out string? name, out bool memory, out bool shared)
        {
            name = null;
            memory = false;
            shared = false;
            if (!dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return;

            string uri = dataSource.Substring("file:".Length);
            int queryIndex = uri.IndexOf('?');
            string rawName = queryIndex < 0 ? uri : uri.Substring(0, queryIndex);
            name = Uri.UnescapeDataString(rawName);

            if (queryIndex < 0) return;
            string[] options = uri.Substring(queryIndex + 1).Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (string option in options)
            {
                int equalsIndex = option.IndexOf('=');
                if (equalsIndex < 0) continue;
                string key = Uri.UnescapeDataString(option.Substring(0, equalsIndex));
                string value = Uri.UnescapeDataString(option.Substring(equalsIndex + 1));
                if (String.Equals(key, "mode", StringComparison.OrdinalIgnoreCase))
                    memory = String.Equals(value, "memory", StringComparison.OrdinalIgnoreCase);
                else if (String.Equals(key, "cache", StringComparison.OrdinalIgnoreCase))
                    shared = String.Equals(value, "shared", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string FileKey(string dataSource)
        {
            if (String.IsNullOrWhiteSpace(dataSource)) throw new InvalidOperationException("The open SQLite connection has no file data source.");

            string path = dataSource;
            if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                ReadUriOptions(path, out string? uriName, out bool _, out bool _);
                if (String.IsNullOrWhiteSpace(uriName)) throw new InvalidOperationException("The open SQLite connection has an invalid file URI data source.");
                path = uriName;
            }

            string fullPath = Path.GetFullPath(path);
            try
            {
                fullPath = ResolveLinks(fullPath);
            }
            catch (IOException)
            {
                // The open data source remains the identity when the platform cannot resolve a link.
            }
            catch (UnauthorizedAccessException)
            {
                // The open data source remains the identity when the platform cannot resolve a link.
            }

            return fullPath;
        }

        private static string ResolveLinks(string fullPath)
        {
            string root = Path.GetPathRoot(fullPath) ?? String.Empty;
            string remainder = fullPath.Substring(root.Length);
            string[] segments = remainder.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            string current = root;

            foreach (string segment in segments)
            {
                current = Path.Combine(current, segment);
                FileSystemInfo item = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                FileSystemInfo? target = item.ResolveLinkTarget(true);
                if (target != null) current = Path.GetFullPath(target.FullName);
            }

            return Path.GetFullPath(current);
        }

        private static Lease? FindActiveLease(Gate gate)
        {
            Lease? lease = _CurrentLease.Value;
            while (lease != null)
            {
                if (ReferenceEquals(lease.Gate, gate) && lease.IsActive) return lease;
                lease = lease.Previous;
            }

            return null;
        }

        private static void RestoreFlow(Lease lease)
        {
            Lease? current = PruneInactive(_CurrentLease.Value);
            if (ReferenceEquals(current, lease)) current = PruneInactive(lease.Previous);
            _CurrentLease.Value = current;
        }

        private static Lease? PruneInactive(Lease? lease)
        {
            while (lease != null && !lease.IsActive) lease = lease.Previous;
            return lease;
        }

        #endregion

        #region Private-Classes

        private sealed class Gate
        {
            private readonly string _Key;
            private readonly object _Sync = new object();
            private readonly LinkedList<Waiter> _Waiters = new LinkedList<Waiter>();
            private Lease? _Holder;

            internal Gate(string key)
            {
                _Key = key;
            }

            internal Task<IDisposable?> Enter(CancellationToken token)
            {
                Lease? active = FindActiveLease(this);
                if (active != null)
                    throw new InvalidOperationException("The SQLite write gate for " + _Key + " is already held by this flow.");

                token.ThrowIfCancellationRequested();
                Lease lease = new Lease(this, PruneInactive(_CurrentLease.Value));
                _CurrentLease.Value = lease;

                Waiter waiter;
                lock (_Sync)
                {
                    if (_Holder == null && _Waiters.Count == 0)
                    {
                        Grant(lease);
                        return Task.FromResult<IDisposable?>(lease);
                    }

                    waiter = new Waiter(lease);
                    waiter.Node = _Waiters.AddLast(waiter);
                }

                if (token.CanBeCanceled)
                {
                    CancellationTokenRegistration registration = token.Register(() => Cancel(waiter, token));
                    bool stillQueued;
                    lock (_Sync)
                    {
                        stillQueued = waiter.Node != null;
                        if (stillQueued) waiter.Registration = registration;
                    }

                    if (!stillQueued) registration.Unregister();
                }

                return waiter.Completion.Task;
            }

            internal void Release(Lease lease)
            {
                Waiter? next = null;
                lock (_Sync)
                {
                    if (!ReferenceEquals(_Holder, lease)) return;
                    _Holder = null;

                    if (_Waiters.Count > 0)
                    {
                        next = _Waiters.First!.Value;
                        _Waiters.RemoveFirst();
                        next.Node = null;
                        Grant(next.Lease);
                    }
                }

                if (next != null)
                {
                    next.Completion.TrySetResult(next.Lease);
                    next.Registration.Unregister();
                }
            }

            private void Cancel(Waiter waiter, CancellationToken token)
            {
                lock (_Sync)
                {
                    if (waiter.Node == null) return;
                    _Waiters.Remove(waiter.Node);
                    waiter.Node = null;
                    waiter.Lease.Cancel();
                }

                waiter.Completion.TrySetCanceled(token);
                waiter.Registration.Unregister();
            }

            private void Grant(Lease lease)
            {
                _Holder = lease;
                lease.MarkGranted();
            }
        }

        private sealed class Waiter
        {
            internal readonly Lease Lease;
            internal readonly TaskCompletionSource<IDisposable?> Completion = new TaskCompletionSource<IDisposable?>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal LinkedListNode<Waiter>? Node;
            internal CancellationTokenRegistration Registration;

            internal Waiter(Lease lease)
            {
                Lease = lease;
            }
        }

        private sealed class Lease : IDisposable
        {
            private const int _Pending = 0;
            private const int _Granted = 1;
            private const int _Disposed = 2;
            private const int _Canceled = 3;
            private Gate? _Gate;
            private int _State = _Pending;

            internal Gate? Gate
            {
                get { return Volatile.Read(ref _Gate); }
            }

            internal Lease? Previous { get; }

            internal bool IsActive
            {
                get { return Volatile.Read(ref _State) == _Granted; }
            }

            internal Lease(Gate gate, Lease? previous)
            {
                _Gate = gate ?? throw new ArgumentNullException(nameof(gate));
                Previous = previous;
            }

            public void Dispose()
            {
                int prior = Interlocked.Exchange(ref _State, _Disposed);
                Gate? gate = Interlocked.Exchange(ref _Gate, null);
                RestoreFlow(this);
                if (prior == _Granted) gate?.Release(this);
            }

            internal void MarkGranted()
            {
                Interlocked.CompareExchange(ref _State, _Granted, _Pending);
            }

            internal void Cancel()
            {
                Interlocked.CompareExchange(ref _State, _Canceled, _Pending);
                Interlocked.Exchange(ref _Gate, null);
            }
        }

        #endregion
    }
}
