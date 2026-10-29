namespace Armada.Server
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Reuses a vessel's git status for a short time. A status read fetches from the remote and counts commits, and
    /// a list refresh asks for every visible vessel, so reading each time starts git processes for work that
    /// has not changed. A read that is already running is shared by the callers that ask for the same vessel,
    /// checkout and branch. The caller authorizes the vessel before it asks, so the cache holds counts only.
    /// </summary>
    public sealed class VesselGitStatusCache
    {
        #region Public-Members

        /// <summary>
        /// How long a status that was read is reused.
        /// </summary>
        public static readonly TimeSpan DefaultSuccessLifetime = TimeSpan.FromMinutes(2);

        /// <summary>
        /// How long a status that failed is reused. It is shorter so a repaired checkout shows its status soon.
        /// </summary>
        public static readonly TimeSpan DefaultErrorLifetime = TimeSpan.FromSeconds(15);

        #endregion

        #region Private-Members

        private sealed class Entry
        {
            public Task<VesselGitStatus> Read = Task.FromResult(new VesselGitStatus());
            public DateTimeOffset? ExpiresUtc = null;
        }

        private readonly Func<string, string, string, Task<VesselGitStatus>> _Reader;
        private readonly TimeProvider _Time;
        private readonly TimeSpan _SuccessLifetime;
        private readonly TimeSpan _ErrorLifetime;
        private readonly Dictionary<string, Entry> _Entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly object _Lock = new object();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="reader">Reads a status from git, given the vessel identifier, its working directory and its
        /// default branch. A failure it reports in <see cref="VesselGitStatus.Error"/> counts as a failed read.</param>
        /// <param name="time">Clock; the system clock when null.</param>
        /// <param name="successLifetime">Lifetime of a status that was read; <see cref="DefaultSuccessLifetime"/> when null.</param>
        /// <param name="errorLifetime">Lifetime of a failed status; <see cref="DefaultErrorLifetime"/> when null.</param>
        public VesselGitStatusCache(
            Func<string, string, string, Task<VesselGitStatus>> reader,
            TimeProvider? time = null,
            TimeSpan? successLifetime = null,
            TimeSpan? errorLifetime = null)
        {
            _Reader = reader ?? throw new ArgumentNullException(nameof(reader));
            _Time = time ?? TimeProvider.System;
            _SuccessLifetime = successLifetime ?? DefaultSuccessLifetime;
            _ErrorLifetime = errorLifetime ?? DefaultErrorLifetime;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Get the status of a vessel's checkout, reading git only when no unexpired status is held and no read is
        /// running for it.
        /// </summary>
        /// <param name="vessel">Vessel the caller may read, with a working directory.</param>
        /// <param name="token">Cancellation token of the caller; it stops this caller's wait, not a shared read.</param>
        /// <returns>The status.</returns>
        public async Task<VesselGitStatus> GetAsync(Vessel vessel, CancellationToken token = default)
        {
            if (vessel == null) throw new ArgumentNullException(nameof(vessel));
            if (String.IsNullOrEmpty(vessel.WorkingDirectory)) throw new ArgumentException("A status read needs a working directory.", nameof(vessel));

            string baseBranch = vessel.DefaultBranch ?? "main";
            string key = vessel.Id + "\n" + vessel.WorkingDirectory + "\n" + baseBranch;
            Entry entry;
            lock (_Lock)
            {
                DateTimeOffset now = _Time.GetUtcNow();
                if (!_Entries.TryGetValue(key, out Entry? held) || (held.ExpiresUtc.HasValue && held.ExpiresUtc.Value <= now))
                {
                    RemoveExpired(now);
                    held = new Entry();
                    held.Read = ReadAsync(held, vessel.Id, vessel.WorkingDirectory, baseBranch);
                    _Entries[key] = held;
                }

                entry = held;
            }

            return await entry.Read.WaitAsync(token).ConfigureAwait(false);
        }

        #endregion

        #region Private-Methods

        // Runs without the caller's token so one caller leaving does not fail the others sharing the read.
        private async Task<VesselGitStatus> ReadAsync(Entry entry, string vesselId, string workingDirectory, string baseBranch)
        {
            VesselGitStatus status;
            try
            {
                status = await Task.Run(() => _Reader(vesselId, workingDirectory, baseBranch)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                status = new VesselGitStatus { VesselId = vesselId, Error = "Git error: " + ex.Message };
            }

            lock (_Lock)
            {
                entry.ExpiresUtc = _Time.GetUtcNow() + (status.Error == null ? _SuccessLifetime : _ErrorLifetime);
            }

            return status;
        }

        private void RemoveExpired(DateTimeOffset now)
        {
            List<string> expired = new List<string>();
            foreach (KeyValuePair<string, Entry> pair in _Entries)
                if (pair.Value.ExpiresUtc.HasValue && pair.Value.ExpiresUtc.Value <= now) expired.Add(pair.Key);
            foreach (string key in expired) _Entries.Remove(key);
        }

        #endregion
    }
}
