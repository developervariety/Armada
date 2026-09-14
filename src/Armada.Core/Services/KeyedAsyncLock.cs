namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Per-key asynchronous mutual exclusion whose entries exist only while a caller holds or
    /// awaits the key. Each entry counts its holders and waiters; the entry is removed when that
    /// count returns to zero, so the number of entries is bounded by concurrent use rather than by
    /// the number of distinct keys ever seen. Callers for one key are serialized; callers for
    /// different keys do not wait for each other.
    /// </summary>
    /// <remarks>
    /// This lock is local to one process. It never replaces a durable cross-instance guard.
    /// </remarks>
    public sealed class KeyedAsyncLock
    {
        #region Public-Members

        /// <summary>
        /// Number of keys currently held or awaited.
        /// </summary>
        public int Count
        {
            get
            {
                lock (_Sync)
                {
                    return _Entries.Count;
                }
            }
        }

        #endregion

        #region Private-Members

        private readonly object _Sync = new object();
        private readonly Dictionary<string, Entry> _Entries;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="comparer">Optional key comparer. Defaults to ordinal comparison.</param>
        public KeyedAsyncLock(IEqualityComparer<string>? comparer = null)
        {
            _Entries = new Dictionary<string, Entry>(comparer ?? StringComparer.Ordinal);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Wait for exclusive use of <paramref name="key"/>. Dispose the returned handle to release it.
        /// A cancelled wait releases its reservation, so an abandoned waiter never leaves an entry behind.
        /// </summary>
        /// <param name="key">Lock key.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A handle that releases the key when disposed.</returns>
        public async Task<IDisposable> AcquireAsync(string key, CancellationToken token = default)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));

            Entry entry;
            lock (_Sync)
            {
                if (!_Entries.TryGetValue(key, out Entry? existing))
                {
                    existing = new Entry();
                    _Entries[key] = existing;
                }
                existing.References++;
                entry = existing;
            }

            try
            {
                await entry.Gate.WaitAsync(token).ConfigureAwait(false);
            }
            catch
            {
                Release(key, entry, false);
                throw;
            }

            return new Handle(this, key, entry);
        }

        #endregion

        #region Private-Methods

        private void Release(string key, Entry entry, bool held)
        {
            lock (_Sync)
            {
                if (held) entry.Gate.Release();
                entry.References--;
                if (entry.References == 0)
                {
                    _Entries.Remove(key);
                    entry.Gate.Dispose();
                }
            }
        }

        #endregion

        private sealed class Entry
        {
            public SemaphoreSlim Gate { get; } = new SemaphoreSlim(1, 1);

            public int References { get; set; }
        }

        private sealed class Handle : IDisposable
        {
            private readonly KeyedAsyncLock _Owner;
            private readonly string _Key;
            private readonly Entry _Entry;
            private int _Released;

            public Handle(KeyedAsyncLock owner, string key, Entry entry)
            {
                _Owner = owner;
                _Key = key;
                _Entry = entry;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _Released, 1) != 0) return;
                _Owner.Release(_Key, _Entry, true);
            }
        }
    }
}
