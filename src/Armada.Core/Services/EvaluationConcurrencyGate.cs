namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Limits how many evaluation runs probe at once, overall and per inference host, so models that share one machine
    /// are evaluated one at a time. Separate from production concurrency. Limits are read on every acquire and release,
    /// so a settings change applies to the next waiter. Waiters are served in arrival order when their host has room.
    /// </summary>
    public class EvaluationConcurrencyGate
    {
        #region Private-Members

        private readonly Func<int> _MaxOverall;
        private readonly Func<int> _MaxPerHost;
        private readonly object _Lock = new object();
        private readonly Dictionary<string, int> _ActiveByHost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<Waiter> _Waiters = new LinkedList<Waiter>();
        private int _Active = 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="maxOverall">Reads the overall limit (values below 1 count as 1).</param>
        /// <param name="maxPerHost">Reads the per-host limit (values below 1 count as 1).</param>
        /// <exception cref="ArgumentNullException">Thrown when either reader is null.</exception>
        public EvaluationConcurrencyGate(Func<int> maxOverall, Func<int> maxPerHost)
        {
            _MaxOverall = maxOverall ?? throw new ArgumentNullException(nameof(maxOverall));
            _MaxPerHost = maxPerHost ?? throw new ArgumentNullException(nameof(maxPerHost));
        }

        #endregion

        #region Public-Members

        /// <summary>
        /// Runs holding a slot now.
        /// </summary>
        public int ActiveCount
        {
            get { lock (_Lock) return _Active; }
        }

        /// <summary>
        /// Runs waiting for a slot.
        /// </summary>
        public int WaitingCount
        {
            get { lock (_Lock) return _Waiters.Count; }
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Runs holding a slot on one host now.
        /// </summary>
        /// <param name="host">Inference host key.</param>
        /// <returns>The count.</returns>
        public int ActiveOn(string host)
        {
            lock (_Lock)
            {
                return _ActiveByHost.TryGetValue(Key(host), out int count) ? count : 0;
            }
        }

        /// <summary>
        /// Wait for a slot on a host. Dispose the returned lease to release it.
        /// </summary>
        /// <param name="host">Inference host key (empty or null shares one "unknown" host).</param>
        /// <param name="token">Cancellation token; cancelling removes the waiter.</param>
        /// <returns>The lease.</returns>
        public async Task<IDisposable> AcquireAsync(string? host, CancellationToken token = default)
        {
            string key = Key(host);
            Waiter waiter = new Waiter(key);
            lock (_Lock)
            {
                // Queued waiters never fit at rest (every release grants all that fit), so room here jumps no one.
                if (HasRoom(key))
                {
                    Take(key);
                    return new Lease(this, key);
                }

                waiter.Node = _Waiters.AddLast(waiter);
            }

            using (token.Register(() => Cancel(waiter)))
            {
                await waiter.Signal.Task.ConfigureAwait(false);
            }

            return new Lease(this, key);
        }

        #endregion

        #region Private-Methods

        private static string Key(string? host)
        {
            return String.IsNullOrWhiteSpace(host) ? "unknown" : host.Trim();
        }

        private bool HasRoom(string key)
        {
            int overall = Math.Max(1, _MaxOverall());
            int perHost = Math.Max(1, _MaxPerHost());
            int onHost = _ActiveByHost.TryGetValue(key, out int count) ? count : 0;
            return _Active < overall && onHost < perHost;
        }

        private void Take(string key)
        {
            _Active++;
            _ActiveByHost[key] = (_ActiveByHost.TryGetValue(key, out int count) ? count : 0) + 1;
        }

        private void Release(string key)
        {
            List<Waiter> granted = new List<Waiter>();
            lock (_Lock)
            {
                _Active = Math.Max(0, _Active - 1);
                if (_ActiveByHost.TryGetValue(key, out int count))
                {
                    if (count <= 1) _ActiveByHost.Remove(key);
                    else _ActiveByHost[key] = count - 1;
                }

                LinkedListNode<Waiter>? node = _Waiters.First;
                while (node != null)
                {
                    LinkedListNode<Waiter>? next = node.Next;
                    if (HasRoom(node.Value.Host))
                    {
                        Take(node.Value.Host);
                        _Waiters.Remove(node);
                        node.Value.Node = null;
                        granted.Add(node.Value);
                    }

                    node = next;
                }
            }

            foreach (Waiter waiter in granted) waiter.Signal.TrySetResult(true);
        }

        private void Cancel(Waiter waiter)
        {
            lock (_Lock)
            {
                if (waiter.Node == null) return;
                _Waiters.Remove(waiter.Node);
                waiter.Node = null;
            }

            waiter.Signal.TrySetCanceled();
        }

        #endregion

        #region Private-Classes

        private class Waiter
        {
            public string Host { get; }
            public TaskCompletionSource<bool> Signal { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public LinkedListNode<Waiter>? Node { get; set; } = null;

            public Waiter(string host)
            {
                Host = host;
            }
        }

        private class Lease : IDisposable
        {
            private readonly EvaluationConcurrencyGate _Gate;
            private readonly string _Host;
            private int _Disposed = 0;

            public Lease(EvaluationConcurrencyGate gate, string host)
            {
                _Gate = gate;
                _Host = host;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _Disposed, 1) == 0) _Gate.Release(_Host);
            }
        }

        #endregion
    }
}
