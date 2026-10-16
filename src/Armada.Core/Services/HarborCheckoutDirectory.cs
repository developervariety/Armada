namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Remembers where each Harbor has vessels checked out, so a turn that runs on a Harbor can be told without waiting
    /// for the Harbor to search its folders. A Harbor's first answers can take tens of seconds (it searches its root
    /// folders and reads each checkout's remotes), so the list is filled when the Harbor links (<see cref="Warm"/>),
    /// answers are kept for <see cref="EntryLifetime"/> (and served while a refresh runs), lookups run a few at a time, and
    /// a lookup that outlasts a caller's wait keeps running and fills the list for the next turn. Thread-safe.
    /// </summary>
    public class HarborCheckoutDirectory
    {
        #region Public-Members

        /// <summary>
        /// How long an answer is used before it is refreshed. Default 10 minutes.
        /// </summary>
        public TimeSpan EntryLifetime { get; set; } = TimeSpan.FromMinutes(10);

        #endregion

        #region Private-Members

        private const int MaxConcurrentLookups = 4;

        private readonly string _Header = "[HarborCheckoutDirectory] ";
        private readonly DatabaseDriver _Database;
        private readonly VesselHostResolver _Hosts;
        private readonly LoggingModule _Logging;
        private readonly SemaphoreSlim _Lookups = new SemaphoreSlim(MaxConcurrentLookups, MaxConcurrentLookups);
        private readonly ConcurrentDictionary<string, CheckoutEntry> _Entries = new ConcurrentDictionary<string, CheckoutEntry>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Task> _InFlight = new ConcurrentDictionary<string, Task>(StringComparer.Ordinal);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database (for a Harbor's vessels when warming).</param>
        /// <param name="hosts">Vessel host resolver (asks a Harbor where a vessel is checked out).</param>
        /// <param name="logging">Logging.</param>
        /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
        public HarborCheckoutDirectory(DatabaseDriver database, VesselHostResolver hosts, LoggingModule logging)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// The checkouts a Harbor has of the given vessels: the remembered answers, after waiting up to
        /// <paramref name="wait"/> for vessels not asked about yet. Lookups still running when the wait ends continue in
        /// the background.
        /// </summary>
        /// <param name="harborId">Harbor identifier.</param>
        /// <param name="vessels">Vessels.</param>
        /// <param name="wait">Longest wait for answers not remembered yet.</param>
        /// <param name="token">Cancellation token (ends the wait, not the lookups).</param>
        /// <returns>The vessels the Harbor has checked out, with their paths.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="vessels"/> is null.</exception>
        public async Task<List<HarborVesselCheckout>> GetAsync(string harborId, List<Vessel> vessels, TimeSpan wait, CancellationToken token = default)
        {
            if (vessels == null) throw new ArgumentNullException(nameof(vessels));
            List<HarborVesselCheckout> found = new List<HarborVesselCheckout>();
            if (String.IsNullOrWhiteSpace(harborId) || vessels.Count == 0) return found;

            DateTime now = DateTime.UtcNow;
            List<Task> missing = new List<Task>();
            foreach (Vessel vessel in vessels)
            {
                bool known = _Entries.TryGetValue(Key(harborId, vessel.Id), out CheckoutEntry? entry);
                if (!known) missing.Add(Refresh(harborId, vessel));
                else if (now - entry!.AnsweredUtc > EntryLifetime) _ = Refresh(harborId, vessel);
            }

            if (missing.Count > 0 && wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.WhenAny(Task.WhenAll(missing), Task.Delay(wait, token)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
            }

            foreach (Vessel vessel in vessels)
            {
                if (!_Entries.TryGetValue(Key(harborId, vessel.Id), out CheckoutEntry? entry) || String.IsNullOrWhiteSpace(entry.Path)) continue;
                found.Add(new HarborVesselCheckout { VesselId = vessel.Id, VesselName = vessel.Name, Path = entry.Path! });
            }

            return found;
        }

        /// <summary>
        /// A Harbor's display name (its name and identifier), or the identifier when it is not connected.
        /// </summary>
        /// <param name="harborId">Harbor identifier.</param>
        /// <returns>The name.</returns>
        public string HarborName(string harborId)
        {
            HarborConnectionManager? harbors = _Hosts.Harbors;
            return harbors != null && !String.IsNullOrWhiteSpace(harborId) ? harbors.Describe(harborId) : harborId;
        }

        /// <summary>
        /// Start learning where a Harbor has its tenant's active vessels checked out (when it links), in the background.
        /// </summary>
        /// <param name="harborId">Harbor identifier.</param>
        public void Warm(string harborId)
        {
            if (String.IsNullOrWhiteSpace(harborId)) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    HarborConnectionManager? harbors = _Hosts.Harbors;
                    if (harbors == null || !harbors.HostsCheckouts(harborId)) return;
                    string? tenantId = harbors.TryGetConnection(harborId, out HarborConnection? connection) ? connection!.TenantId : null;
                    List<Vessel> vessels = String.IsNullOrEmpty(tenantId)
                        ? await _Database.Vessels.EnumerateAsync().ConfigureAwait(false)
                        : await _Database.Vessels.EnumerateAsync(tenantId!).ConfigureAwait(false);
                    List<Task> lookups = vessels.Where(v => v.Active).Select(v => Refresh(harborId, v)).ToList();
                    await Task.WhenAll(lookups).ConfigureAwait(false);
                    int checkouts = vessels.Count(v => _Entries.TryGetValue(Key(harborId, v.Id), out CheckoutEntry? e) && !String.IsNullOrWhiteSpace(e.Path));
                    _Logging.Info(_Header + "harbor " + harborId + " has " + checkouts + " of " + lookups.Count + " active vessel(s) checked out");
                }
                catch (Exception ex)
                {
                    _Logging.Warn(_Header + "could not learn the checkouts of harbor " + harborId + ": " + ex.Message);
                }
            });
        }

        #endregion

        #region Private-Methods

        private static string Key(string harborId, string vesselId)
        {
            return harborId + "|" + vesselId;
        }

        private Task Refresh(string harborId, Vessel vessel)
        {
            // One lookup per Harbor and vessel at a time; a finished one is replaced. Under contention a duplicate lookup
            // may start, which is harmless.
            string key = Key(harborId, vessel.Id);
            return _InFlight.AddOrUpdate(
                key,
                _ => LookupAsync(harborId, vessel, key),
                (_, existing) => existing.IsCompleted ? LookupAsync(harborId, vessel, key) : existing);
        }

        private async Task LookupAsync(string harborId, Vessel vessel, string key)
        {
            await Task.Yield();
            try
            {
                await _Lookups.WaitAsync().ConfigureAwait(false);
                try
                {
                    HarborCheckoutLookup lookup = await _Hosts.FindHarborCheckoutAsync(harborId, vessel).ConfigureAwait(false);
                    // An unanswered lookup is not remembered, so the next caller asks again.
                    if (lookup.Answered) _Entries[key] = new CheckoutEntry { Path = lookup.Path, AnsweredUtc = DateTime.UtcNow };
                }
                finally
                {
                    _Lookups.Release();
                }
            }
            catch (Exception ex)
            {
                _Logging.Debug(_Header + "lookup of vessel " + vessel.Name + " on harbor " + harborId + " failed: " + ex.Message);
            }
        }

        #endregion

        #region Private-Classes

        private sealed class CheckoutEntry
        {
            public string? Path { get; set; } = null;

            public DateTime AnsweredUtc { get; set; } = DateTime.UtcNow;
        }

        #endregion
    }
}
