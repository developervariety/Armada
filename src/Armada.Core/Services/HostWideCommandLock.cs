namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Host-wide execution slots for expensive build and test commands.
    /// </summary>
    /// <remarks>
    /// The DoD gate, check runs, and merge-queue test runs all execute a vessel's full build and
    /// unit-test command. More of those at once than the host can carry produce failures that look
    /// like broken code but are not, and each contended run stretches wall time beyond the
    /// quiet-window baseline. The resource they contend for is the machine's CPU and disk, not any
    /// single vessel, so the interlock is host-wide: every caller that runs an expensive command must
    /// acquire a slot here. The host grants at most <see cref="SlotCount"/> slots at once, one by
    /// default, so by default it runs one full suite at a time. Callers must hold a dock lease or
    /// other liveness guarantee across the wait, because a request can sit behind other callers'
    /// full build and test runs.
    ///
    /// Slots are granted strictly in request order. A check or merge-queue test holds a slot for one
    /// command; a definition-of-done gate takes one at its first command and holds it until the gate
    /// ends, so it is never sent to the back of the queue between its build, tests and consumer
    /// commands. A request that asked before a gate's first command is still granted first. Raising the slot count grants waiting requests at once, in order; lowering it
    /// takes no slot from a holder and grants nothing new until the holders drop below the new count.
    /// Every request names its owner, so <see cref="Snapshot"/> can say who holds each slot and where
    /// each waiter stands.
    ///
    /// A free slot is not enough on its own: the next request is granted only while the host has at
    /// least <see cref="MemoryFloorMb"/> megabytes of available memory, because one full suite can use
    /// several gigabytes and a host without swap answers an overcommit by killing test processes. A
    /// floor of 0 turns the check off, and an unknown reading (a host without <c>/proc/meminfo</c>, or a
    /// reading that cannot be parsed) never holds a request. Memory changes without any lock event, so
    /// while the first request in line is held only for memory the lock reads memory again every
    /// <see cref="MemoryRecheckInterval"/>. A request held for memory keeps its place: no later request
    /// is granted ahead of it. Memory used by processes outside the lock can stay below the floor
    /// indefinitely, so when no slot is held and the first request has been held for memory for
    /// <see cref="MaximumMemoryWait"/>, it is granted anyway and recorded as granted under the floor.
    /// </remarks>
    public static class HostWideCommandLock
    {
        #region Public-Members

        /// <summary>
        /// Fewest slots the host grants at once.
        /// </summary>
        public const int MinimumSlots = 1;

        /// <summary>
        /// Most slots the host grants at once.
        /// </summary>
        public const int MaximumSlots = 8;

        /// <summary>
        /// Largest memory floor in megabytes.
        /// </summary>
        public const int MaximumMemoryFloorMb = 1048576;

        /// <summary>
        /// Memory floor in megabytes the settings default to.
        /// </summary>
        public const int DefaultMemoryFloorMb = 6144;

        /// <summary>
        /// How often the lock reads memory again while the first request in line is held only for memory.
        /// </summary>
        public static readonly TimeSpan MemoryRecheckInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long the first request in line may be held for memory while no slot is held before it is
        /// granted under the floor.
        /// </summary>
        public static readonly TimeSpan MaximumMemoryWait = TimeSpan.FromMinutes(15);

        /// <summary>
        /// How many requests may hold a slot at once. Defaults to 1.
        /// </summary>
        public static int SlotCount
        {
            get
            {
                lock (_Sync)
                {
                    return _SlotCount;
                }
            }
        }

        /// <summary>
        /// Available memory in megabytes the host must have before the next request is granted. 0 turns
        /// the check off. The lock starts at 0; the admiral applies the configured floor at startup and on
        /// every settings reload.
        /// </summary>
        public static int MemoryFloorMb
        {
            get
            {
                lock (_Sync)
                {
                    return _MemoryFloorMb;
                }
            }
        }

        /// <summary>
        /// The function that reads the host's available memory in megabytes, or returns null when the
        /// reading is unknown.
        /// </summary>
        public static Func<long?> MemoryReader
        {
            get
            {
                lock (_Sync)
                {
                    return _MemoryReader;
                }
            }
        }

        #endregion

        #region Private-Members

        private static readonly object _Sync = new object();
        private static readonly LinkedList<Request> _Waiters = new LinkedList<Request>();
        private static readonly List<Request> _Holders = new List<Request>();
        private static int _SlotCount = MinimumSlots;
        private static int _MemoryFloorMb = 0;
        private static Func<long?> _MemoryReader = ReadLinuxAvailableMemoryMb;
        private static Func<DateTime> _Clock = DefaultClock;
        private static long? _LastAvailableMemoryMb = null;
        private static DateTime? _LastMemoryReadUtc = null;
        private static bool _HeadHeldForMemory = false;
        private static long _UnderFloorGrants = 0;
        private static Timer? _RecheckTimer = null;
        private static bool _RecheckArmed = false;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Clamp a slot count to [<see cref="MinimumSlots"/>, <see cref="MaximumSlots"/>].
        /// </summary>
        /// <param name="slots">Requested slot count.</param>
        /// <returns>The clamped count.</returns>
        public static int ClampSlotCount(int slots)
        {
            return Math.Max(MinimumSlots, Math.Min(MaximumSlots, slots));
        }

        /// <summary>
        /// Set how many requests may hold a slot at once. The count is clamped to
        /// [<see cref="MinimumSlots"/>, <see cref="MaximumSlots"/>]. A raised count grants waiting
        /// requests at once, in request order; a lowered count takes no slot from a current holder.
        /// </summary>
        /// <param name="slots">Requested slot count.</param>
        public static void SetSlotCount(int slots)
        {
            List<Request> granted = new List<Request>();
            lock (_Sync)
            {
                _SlotCount = ClampSlotCount(slots);
                GrantWaiting(granted);
            }

            Complete(granted);
        }

        /// <summary>
        /// Clamp a memory floor to [0, <see cref="MaximumMemoryFloorMb"/>].
        /// </summary>
        /// <param name="floorMb">Requested floor in megabytes.</param>
        /// <returns>The clamped floor.</returns>
        public static int ClampMemoryFloorMb(int floorMb)
        {
            return Math.Max(0, Math.Min(MaximumMemoryFloorMb, floorMb));
        }

        /// <summary>
        /// Set the available memory in megabytes the host must have before the next request is granted.
        /// The floor is clamped to [0, <see cref="MaximumMemoryFloorMb"/>]; 0 turns the check off. A lowered
        /// floor grants waiting requests at once, in request order.
        /// </summary>
        /// <param name="floorMb">Requested floor in megabytes.</param>
        public static void SetMemoryFloorMb(int floorMb)
        {
            List<Request> granted = new List<Request>();
            lock (_Sync)
            {
                _MemoryFloorMb = ClampMemoryFloorMb(floorMb);
                GrantWaiting(granted);
            }

            Complete(granted);
        }

        /// <summary>
        /// Set the function that reads the host's available memory in megabytes. It returns null when the
        /// reading is unknown; a reader that throws or returns a negative value counts as unknown.
        /// </summary>
        /// <param name="reader">The reader, or null for the production reader of <c>/proc/meminfo</c>.</param>
        public static void SetMemoryReader(Func<long?>? reader)
        {
            List<Request> granted = new List<Request>();
            lock (_Sync)
            {
                _MemoryReader = reader ?? ReadLinuxAvailableMemoryMb;
                GrantWaiting(granted);
            }

            Complete(granted);
        }

        /// <summary>
        /// Set the clock that dates requests, grants and memory holds.
        /// </summary>
        /// <param name="utcNow">A function that returns the current UTC time, or null for the system clock.</param>
        public static void SetClock(Func<DateTime>? utcNow)
        {
            lock (_Sync)
            {
                _Clock = utcNow ?? DefaultClock;
            }
        }

        /// <summary>
        /// Read memory again and grant the waiting requests the reading now admits. The lock calls this on
        /// a timer while the first request in line is held only for memory.
        /// </summary>
        public static void RecheckMemory()
        {
            List<Request> granted = new List<Request>();
            lock (_Sync)
            {
                GrantWaiting(granted);
            }

            Complete(granted);
        }

        /// <summary>
        /// Read <c>MemAvailable</c> from the text of <c>/proc/meminfo</c>.
        /// </summary>
        /// <param name="content">The file's text.</param>
        /// <returns>Available memory in megabytes, or null when the line is absent or cannot be parsed.</returns>
        public static long? ParseMemAvailableMb(string? content)
        {
            if (String.IsNullOrEmpty(content)) return null;
            const string label = "MemAvailable:";
            string[] lines = content.Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (!line.StartsWith(label, StringComparison.Ordinal)) continue;

                string[] parts = line.Substring(label.Length).Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return null;
                if (!Int64.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long value)) return null;

                string unit = parts.Length > 1 ? parts[1] : "kB";
                if (String.Equals(unit, "kB", StringComparison.OrdinalIgnoreCase)) return value / 1024;
                return null;
            }

            return null;
        }

        /// <summary>
        /// Acquire a host-wide execution slot for an unnamed caller.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A disposable lease; dispose it to release the slot.</returns>
        public static Task<IDisposable> AcquireAsync(CancellationToken token = default)
        {
            return AcquireAsync(null, null, token);
        }

        /// <summary>
        /// Acquire a host-wide execution slot. Requests are granted in the order they were made.
        /// </summary>
        /// <param name="ownerKey">Stable key of the requester, such as a check run ID, used to find its place in the queue.</param>
        /// <param name="ownerDescription">What the requester runs, shown to anyone waiting behind it.</param>
        /// <param name="token">Cancellation token. Cancelling a waiting request removes it from the queue.</param>
        /// <returns>A disposable lease; dispose it to release the slot.</returns>
        /// <remarks>The time from request to grant or cancellation is recorded in <see cref="ArmadaMetrics.HostSlotWait"/>.</remarks>
        public static async Task<IDisposable> AcquireAsync(string? ownerKey, string? ownerDescription, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            LinkedListNode<Request>? node = null;
            Request request;
            List<Request> granted = new List<Request>();
            Stopwatch wait = Stopwatch.StartNew();

            lock (_Sync)
            {
                request = new Request(ownerKey, ownerDescription, _Clock());
                node = _Waiters.AddLast(request);
                GrantWaiting(granted);
            }

            Complete(granted);
            if (granted.Contains(request))
            {
                ArmadaMetrics.RecordHostSlotWait(wait.Elapsed, true);
                return new Lease(request);
            }

            try
            {
                using (token.Register(() => CancelWaiting(node)))
                {
                    await request.Granted.Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                ArmadaMetrics.RecordHostSlotWait(wait.Elapsed, false);
                throw;
            }

            ArmadaMetrics.RecordHostSlotWait(wait.Elapsed, true);
            return new Lease(request);
        }

        /// <summary>
        /// Who holds the slots and who waits for one, in grant order, and whether free memory holds the
        /// first request in line.
        /// </summary>
        /// <returns>A point-in-time view of the slots.</returns>
        public static HostSlotSnapshot Snapshot()
        {
            lock (_Sync)
            {
                HostSlotSnapshot snapshot = new HostSlotSnapshot();
                snapshot.SlotCount = _SlotCount;
                snapshot.MemoryFloorMb = _MemoryFloorMb;
                snapshot.AvailableMemoryMb = _LastAvailableMemoryMb;
                snapshot.AvailableMemoryReadUtc = _LastMemoryReadUtc;
                snapshot.UnderFloorGrants = _UnderFloorGrants;
                if (_HeadHeldForMemory && _Waiters.First != null && _LastAvailableMemoryMb.HasValue)
                {
                    snapshot.HeldForMemory = true;
                    snapshot.MemoryHeldSinceUtc = _Waiters.First.Value.MemoryHeldSinceUtc;
                    snapshot.MemoryWaitReason = DescribeMemoryWait(_LastAvailableMemoryMb.Value, _MemoryFloorMb);
                }

                foreach (Request holder in _Holders)
                {
                    snapshot.Holders.Add(new HostSlotHolder
                    {
                        Key = holder.OwnerKey,
                        Description = holder.OwnerDescription,
                        SinceUtc = holder.GrantedUtc,
                        GrantedUnderMemoryFloor = holder.GrantedUnderMemoryFloor
                    });
                }

                if (_Holders.Count > 0)
                {
                    Request earliest = _Holders[0];
                    snapshot.HolderKey = earliest.OwnerKey;
                    snapshot.HolderDescription = earliest.OwnerDescription;
                    snapshot.HolderSinceUtc = earliest.GrantedUtc;
                }

                int position = 1;
                foreach (Request waiter in _Waiters)
                {
                    snapshot.Waiters.Add(new HostSlotWaiter
                    {
                        Position = position++,
                        Key = waiter.OwnerKey,
                        Description = waiter.OwnerDescription,
                        RequestedUtc = waiter.RequestedUtc
                    });
                }

                return snapshot;
            }
        }

        #endregion

        #region Private-Methods

        private static void CancelWaiting(LinkedListNode<Request>? node)
        {
            if (node == null) return;
            List<Request> granted = new List<Request>();
            lock (_Sync)
            {
                // A request granted just before cancellation keeps its slot; its caller releases it.
                if (node.List == null) return;
                _Waiters.Remove(node);

                // The request behind a cancelled first request is evaluated at once, so the memory hold
                // the snapshot reports always describes the request now first in line.
                GrantWaiting(granted);
            }

            node.Value.Granted.TrySetCanceled();
            Complete(granted);
        }

        private static void Release(Request request)
        {
            List<Request> granted = new List<Request>();
            lock (_Sync)
            {
                if (!_Holders.Remove(request)) return;
                GrantWaiting(granted);
            }

            Complete(granted);
        }

        /// <summary>
        /// Move waiters to holders, first in line first, while a slot is free and memory admits the first
        /// request. A request held for memory ends the loop, so no later request passes it. Call under
        /// <see cref="_Sync"/>; complete the returned requests outside it.
        /// </summary>
        private static void GrantWaiting(List<Request> granted)
        {
            _HeadHeldForMemory = false;
            while (_Holders.Count < _SlotCount && _Waiters.First != null)
            {
                Request next = _Waiters.First.Value;
                DateTime now = _Clock();
                bool underFloor = false;
                if (_MemoryFloorMb > 0)
                {
                    long? available = ReadAvailableMemory(now);
                    if (available.HasValue && available.Value < _MemoryFloorMb)
                    {
                        if (!next.MemoryHeldSinceUtc.HasValue) next.MemoryHeldSinceUtc = now;
                        bool starved = _Holders.Count == 0 && now - next.MemoryHeldSinceUtc.Value >= MaximumMemoryWait;
                        if (!starved)
                        {
                            _HeadHeldForMemory = true;
                            break;
                        }

                        underFloor = true;
                    }
                }

                _Waiters.RemoveFirst();
                next.GrantedUtc = now;
                next.GrantedUnderMemoryFloor = underFloor;
                if (underFloor) _UnderFloorGrants++;
                _Holders.Add(next);
                granted.Add(next);
            }

            UpdateRecheckTimer(_HeadHeldForMemory);
        }

        /// <summary>
        /// Read available memory through the configured reader and remember the reading. Call under
        /// <see cref="_Sync"/>.
        /// </summary>
        /// <returns>Available memory in megabytes, or null when unknown.</returns>
        private static long? ReadAvailableMemory(DateTime now)
        {
            long? available;
            try
            {
                available = _MemoryReader();
            }
            catch (Exception)
            {
                // A reader that fails gives an unknown reading, which never holds a request; the snapshot
                // shows it as a null AvailableMemoryMb.
                available = null;
            }

            if (available.HasValue && available.Value < 0) available = null;
            _LastAvailableMemoryMb = available;
            _LastMemoryReadUtc = now;
            return available;
        }

        /// <summary>
        /// Run the memory re-check timer while the first request in line is held only for memory, and stop
        /// it otherwise. Call under <see cref="_Sync"/>.
        /// </summary>
        private static void UpdateRecheckTimer(bool needed)
        {
            if (needed == _RecheckArmed) return;
            if (needed)
            {
                if (_RecheckTimer == null)
                {
                    _RecheckTimer = new Timer(_ => RecheckMemory(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }

                _RecheckTimer.Change(MemoryRecheckInterval, MemoryRecheckInterval);
            }
            else if (_RecheckTimer != null)
            {
                _RecheckTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            _RecheckArmed = needed;
        }

        /// <summary>
        /// Name the memory hold for an operator.
        /// </summary>
        private static string DescribeMemoryWait(long availableMb, int floorMb)
        {
            return "waiting for free memory: "
                + availableMb.ToString("N0", CultureInfo.InvariantCulture) + " MB available, floor "
                + floorMb.ToString("N0", CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary>
        /// Read <c>MemAvailable</c> from <c>/proc/meminfo</c> on Linux. Returns null on any other platform
        /// and on any read or parse failure, so an unknown reading never holds a request.
        /// </summary>
        private static long? ReadLinuxAvailableMemoryMb()
        {
            if (!OperatingSystem.IsLinux()) return null;
            try
            {
                return ParseMemAvailableMb(File.ReadAllText("/proc/meminfo"));
            }
            catch (Exception)
            {
                // An unreadable file is an unknown reading; the snapshot shows it as a null AvailableMemoryMb.
                return null;
            }
        }

        private static DateTime DefaultClock()
        {
            return DateTime.UtcNow;
        }

        private static void Complete(List<Request> granted)
        {
            foreach (Request request in granted)
            {
                request.Granted.TrySetResult(true);
            }
        }

        #endregion

        #region Private-Classes

        private sealed class Request
        {
            public Request(string? ownerKey, string? ownerDescription, DateTime requestedUtc)
            {
                OwnerKey = String.IsNullOrWhiteSpace(ownerKey) ? null : ownerKey.Trim();
                OwnerDescription = String.IsNullOrWhiteSpace(ownerDescription) ? "an unnamed build or test command" : ownerDescription.Trim();
                RequestedUtc = requestedUtc;
            }

            public string? OwnerKey { get; }

            public string OwnerDescription { get; }

            public DateTime RequestedUtc { get; }

            public DateTime? GrantedUtc { get; set; }

            public DateTime? MemoryHeldSinceUtc { get; set; }

            public bool GrantedUnderMemoryFloor { get; set; }

            public TaskCompletionSource<bool> Granted { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class Lease : IDisposable
        {
            private readonly Request _Request;
            private int _Released = 0;

            public Lease(Request request)
            {
                _Request = request ?? throw new ArgumentNullException(nameof(request));
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _Released, 1) == 0)
                {
                    Release(_Request);
                }
            }
        }

        #endregion
    }
}
