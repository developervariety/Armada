namespace Armada.Server
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;

    /// <summary>
    /// The REST requests the admiral has started and not yet answered. The request log is written when a
    /// request completes, so a request that is still running when the process dies leaves no line; this
    /// registry lets the memory watch name it while it runs. It holds at most <see cref="MaxEntries"/>
    /// requests and drops entries older than <see cref="StaleAfter"/> when full, so a request whose end
    /// is never reported cannot grow it.
    /// </summary>
    public sealed class InFlightRequestRegistry
    {
        #region Public-Members

        /// <summary>Most requests held at once.</summary>
        public const int MaxEntries = 256;

        /// <summary>Age after which an entry is dropped when the registry is full.</summary>
        public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

        /// <summary>Number of requests held.</summary>
        public int Count => _Requests.Count;

        #endregion

        #region Private-Members

        private readonly ConcurrentDictionary<object, InFlightRequest> _Requests = new ConcurrentDictionary<object, InFlightRequest>(ReferenceEqualityComparer.Instance);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record that a request started.
        /// </summary>
        /// <param name="key">Object that identifies the request until it ends, such as its HTTP context.</param>
        /// <param name="method">HTTP method.</param>
        /// <param name="url">Request URL with its query string.</param>
        /// <param name="startUtc">When the request started.</param>
        public void Begin(object key, string method, string url, DateTime startUtc)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (_Requests.Count >= MaxEntries)
            {
                foreach (KeyValuePair<object, InFlightRequest> entry in _Requests)
                {
                    if (startUtc - entry.Value.StartUtc >= StaleAfter)
                        _Requests.TryRemove(entry.Key, out InFlightRequest? _);
                }

                if (_Requests.Count >= MaxEntries) return;
            }

            _Requests[key] = new InFlightRequest(method, url, startUtc);
        }

        /// <summary>
        /// Record that a request ended.
        /// </summary>
        /// <param name="key">The object passed to <see cref="Begin"/>.</param>
        public void End(object key)
        {
            if (key == null) return;
            _Requests.TryRemove(key, out InFlightRequest? _);
        }

        /// <summary>
        /// The requests running now, oldest first.
        /// </summary>
        /// <returns>A snapshot of the running requests.</returns>
        public List<InFlightRequest> Snapshot()
        {
            return _Requests.Values.OrderBy(request => request.StartUtc).ToList();
        }

        /// <summary>
        /// Describe the running requests, oldest first, for example
        /// <c>POST /api/v1/history/enumerate (52 s), GET /api/v1/status (3 s)</c>.
        /// </summary>
        /// <param name="nowUtc">Current time, used for each request's age.</param>
        /// <param name="maxRequests">Most requests named; the rest are counted.</param>
        /// <returns>The description, or <c>none</c> when no request is running.</returns>
        public string Describe(DateTime nowUtc, int maxRequests)
        {
            List<InFlightRequest> running = Snapshot();
            if (running.Count == 0) return "none";

            int shown = Math.Max(1, maxRequests);
            List<string> parts = new List<string>();
            foreach (InFlightRequest request in running.Take(shown))
            {
                double seconds = Math.Max(0, (nowUtc - request.StartUtc).TotalSeconds);
                parts.Add(request.Method + " " + request.Url + " (" + seconds.ToString("F0", CultureInfo.InvariantCulture) + " s)");
            }

            string text = String.Join(", ", parts);
            if (running.Count > shown)
                text += " and " + (running.Count - shown).ToString(CultureInfo.InvariantCulture) + " more";
            return text;
        }

        #endregion
    }
}
