namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;

    /// <summary>
    /// Short-lived cache of vessel git status. Reading it runs git fetch and two git rev-list commands in the vessel's
    /// checkout (on a Harbor, three processes on the developer's machine), and list screens ask for every vessel on
    /// every refresh. A result is reused for a while, and concurrent requests for the same key share one read.
    /// </summary>
    public class VesselGitStatusCache
    {
        #region Public-Members

        /// <summary>
        /// Default time a result with counts is reused.
        /// </summary>
        public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Default time a result with an error is reused (shorter, so a Harbor that reconnects shows up quickly).
        /// </summary>
        public static readonly TimeSpan DefaultErrorLifetime = TimeSpan.FromSeconds(15);

        #endregion

        #region Private-Members

        private readonly TimeSpan _Lifetime;
        private readonly TimeSpan _ErrorLifetime;
        private readonly Func<DateTime> _Clock;
        private readonly object _Lock = new object();
        private readonly Dictionary<string, Entry> _Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with the default lifetimes.
        /// </summary>
        public VesselGitStatusCache() : this(DefaultLifetime, DefaultErrorLifetime, null)
        {
        }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="lifetime">Time a result with counts is reused.</param>
        /// <param name="errorLifetime">Time a result with an error is reused.</param>
        /// <param name="clock">UTC clock, or null for the system clock.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when a lifetime is negative.</exception>
        public VesselGitStatusCache(TimeSpan lifetime, TimeSpan errorLifetime, Func<DateTime>? clock)
        {
            if (lifetime < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
            if (errorLifetime < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(errorLifetime));
            _Lifetime = lifetime;
            _ErrorLifetime = errorLifetime;
            _Clock = clock ?? (() => DateTime.UtcNow);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Return the cached status for a key while it is fresh, join a read already in progress, or start a new read.
        /// A read that throws is not cached.
        /// </summary>
        /// <param name="key">Cache key (the vessel and the user, since the checkout used can depend on the user).</param>
        /// <param name="read">Reads the status.</param>
        /// <returns>The status.</returns>
        /// <exception cref="ArgumentNullException">Thrown when key or read is null.</exception>
        public Task<VesselGitStatusResponse> GetAsync(string key, Func<Task<VesselGitStatusResponse>> read)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (read == null) throw new ArgumentNullException(nameof(read));

            lock (_Lock)
            {
                DateTime now = _Clock();
                if (_Entries.TryGetValue(key, out Entry? existing) && IsUsable(existing, now)) return existing.Read;

                PruneExpired(now);
                Entry entry = new Entry();
                entry.Read = RunAsync(key, entry, read);
                // A read that completed synchronously has already recorded itself (or removed itself on failure).
                if (!entry.Read.IsCompleted) _Entries[key] = entry;
                return entry.Read;
            }
        }

        /// <summary>
        /// Drop every cached status.
        /// </summary>
        public void Clear()
        {
            lock (_Lock) _Entries.Clear();
        }

        #endregion

        #region Private-Methods

        private async Task<VesselGitStatusResponse> RunAsync(string key, Entry entry, Func<Task<VesselGitStatusResponse>> read)
        {
            VesselGitStatusResponse result;
            try
            {
                result = await read().ConfigureAwait(false);
            }
            catch
            {
                lock (_Lock)
                {
                    if (_Entries.TryGetValue(key, out Entry? current) && ReferenceEquals(current, entry)) _Entries.Remove(key);
                }
                throw;
            }

            lock (_Lock)
            {
                entry.CompletedUtc = _Clock();
                entry.Failed = result.Error != null;
                _Entries[key] = entry;
            }
            return result;
        }

        private bool IsUsable(Entry entry, DateTime now)
        {
            if (entry.CompletedUtc == null) return true;
            TimeSpan lifetime = entry.Failed ? _ErrorLifetime : _Lifetime;
            return now - entry.CompletedUtc.Value < lifetime;
        }

        private void PruneExpired(DateTime now)
        {
            List<string> expired = new List<string>();
            foreach (KeyValuePair<string, Entry> pair in _Entries)
                if (!IsUsable(pair.Value, now)) expired.Add(pair.Key);
            foreach (string key in expired) _Entries.Remove(key);
        }

        #endregion

        private sealed class Entry
        {
            public Task<VesselGitStatusResponse> Read { get; set; } = Task.FromResult(new VesselGitStatusResponse());

            public DateTime? CompletedUtc { get; set; } = null;

            public bool Failed { get; set; } = false;
        }
    }
}
