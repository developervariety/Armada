namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
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
    ///
    /// A lease also says whether it shared the host: <see cref="ReadShareMark"/> marks a point in time and
    /// <see cref="SharedSince"/> answers whether another request held a slot at the same time as the lease at any
    /// moment after that mark. A command that hit its time limit while it shared the host may have spent that time
    /// on the other command's load or on a lock both commands take.
    ///
    /// A request can ask to hold the host alone (<see cref="AcquireAloneAsync"/>). It keeps its place in line;
    /// when it is first, it is granted only once no slot is held, and while it holds, no other request is granted.
    /// A request behind it waits, so a request that holds the host alone cannot be starved by later requests.
    ///
    /// A request may name a vessel. A vessel's full suite is serialized outside Armada by a file lock the vessel's
    /// own command takes (a <c>flock</c> around the suite), so two of one vessel's suite commands cannot run at
    /// once however many host slots are free: the second blocks inside its command on that file lock. A request
    /// that named its vessel first takes an in-process per-vessel suite slot and only then queues for a host slot, so a
    /// command that would just block on its vessel's suite lock never occupies a host slot another vessel's command
    /// could use. The host grants at most <see cref="VesselSlotCount"/> of one vessel's suite commands at once, one
    /// by default. The per-vessel slot is always taken before the host slot and released after it, and a request
    /// holds at most one, so the order never cycles into a deadlock. A request that names no vessel queues for a
    /// slot directly. A captain that runs a full suite takes the same slots through the suite lease.
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
        /// How many of one vessel's suite commands may run at once. Defaults to 1.
        /// </summary>
        public static int VesselSlotCount
        {
            get
            {
                lock (_Sync)
                {
                    return _VesselSlotCount;
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
        private static int _VesselSlotCount = MinimumSlots;
        private static readonly ConcurrentDictionary<string, VesselQueue> _VesselQueues =
            new ConcurrentDictionary<string, VesselQueue>(StringComparer.Ordinal);

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
        /// Set how many of one vessel's suite commands may run at once. The count is clamped to
        /// [<see cref="MinimumSlots"/>, <see cref="MaximumSlots"/>]. A raised count grants waiting
        /// vessel requests at once, in request order; a lowered count takes no slot from a current holder.
        /// </summary>
        /// <param name="slots">Requested per-vessel slot count.</param>
        public static void SetVesselSlotCount(int slots)
        {
            List<Request> granted = new List<Request>();
            lock (_Sync)
            {
                _VesselSlotCount = ClampSlotCount(slots);
                foreach (VesselQueue queue in _VesselQueues.Values)
                {
                    GrantVessel(queue, granted);
                }
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
        public static Task<IDisposable> AcquireAsync(string? ownerKey, string? ownerDescription, CancellationToken token = default)
        {
            return AcquireCoreAsync(ownerKey, ownerDescription, false, null, token);
        }

        /// <summary>
        /// Acquire a host-wide execution slot for a command that runs one vessel's suite. The request takes the
        /// vessel's in-process suite lock first and only then queues for a slot, so it never holds a slot while it
        /// waits for another of the same vessel's suite commands to finish. Requests are granted in the order they
        /// reach the slot queue.
        /// </summary>
        /// <param name="ownerKey">Stable key of the requester, such as a check run ID, used to find its place in the queue.</param>
        /// <param name="ownerDescription">What the requester runs, shown to anyone waiting behind it.</param>
        /// <param name="vesselKey">The vessel whose suite this command runs, or null to queue for a slot directly.</param>
        /// <param name="token">Cancellation token. Cancelling removes a waiting request from the queue or the vessel lock.</param>
        /// <returns>A disposable lease; dispose it to release the slot and then the vessel lock.</returns>
        public static Task<IDisposable> AcquireAsync(string? ownerKey, string? ownerDescription, string? vesselKey, CancellationToken token)
        {
            return AcquireCoreAsync(ownerKey, ownerDescription, false, vesselKey, token);
        }

        /// <summary>
        /// Acquire the whole host: a slot granted only when no other request holds one, and kept while no other
        /// request is granted. The request keeps its place in line; requests behind it wait until it is released.
        /// </summary>
        /// <param name="ownerKey">Stable key of the requester, used to find its place in the queue.</param>
        /// <param name="ownerDescription">What the requester runs, shown to anyone waiting behind it.</param>
        /// <param name="token">Cancellation token. Cancelling a waiting request removes it from the queue.</param>
        /// <returns>A disposable lease; dispose it to release the host.</returns>
        public static Task<IDisposable> AcquireAloneAsync(string? ownerKey, string? ownerDescription, CancellationToken token = default)
        {
            return AcquireCoreAsync(ownerKey, ownerDescription, true, null, token);
        }

        /// <summary>
        /// Acquire the whole host for a command that runs one vessel's suite, taking the vessel's in-process suite
        /// lock first and only then queuing for the host. See <see cref="AcquireAloneAsync(string?, string?, CancellationToken)"/>.
        /// </summary>
        /// <param name="ownerKey">Stable key of the requester, used to find its place in the queue.</param>
        /// <param name="ownerDescription">What the requester runs, shown to anyone waiting behind it.</param>
        /// <param name="vesselKey">The vessel whose suite this command runs, or null to queue for the host directly.</param>
        /// <param name="token">Cancellation token. Cancelling removes a waiting request from the queue or the vessel lock.</param>
        /// <returns>A disposable lease; dispose it to release the host and then the vessel lock.</returns>
        public static Task<IDisposable> AcquireAloneAsync(string? ownerKey, string? ownerDescription, string? vesselKey, CancellationToken token)
        {
            return AcquireCoreAsync(ownerKey, ownerDescription, true, vesselKey, token);
        }

        private static async Task<IDisposable> AcquireCoreAsync(string? ownerKey, string? ownerDescription, bool alone, string? vesselKey, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Stopwatch wait = Stopwatch.StartNew();

            // A command that runs a vessel's suite takes that vessel's in-process suite slot before it queues for a
            // host slot. A command that would then only block inside itself on the vessel's own suite file lock never
            // occupies a host slot in the meantime. The vessel slot is always taken here, before the host slot, and
            // released when the lease is disposed, after the host slot: one per request, so the order cannot cycle
            // into a deadlock.
            string? vessel = String.IsNullOrWhiteSpace(vesselKey) ? null : vesselKey.Trim();
            IDisposable? vesselLease = null;
            if (vessel != null)
            {
                try
                {
                    vesselLease = await AcquireVesselAsync(vessel, ownerKey, ownerDescription, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ArmadaMetrics.RecordHostSlotWait(wait.Elapsed, false);
                    throw;
                }
            }

            LinkedListNode<Request>? node = null;
            Request request;
            List<Request> granted = new List<Request>();

            lock (_Sync)
            {
                request = new Request(ownerKey, ownerDescription, _Clock()) { Alone = alone, AheadAtRequest = _Waiters.Count };
                node = _Waiters.AddLast(request);
                GrantWaiting(granted);

                // The holders when the request has to wait are the ones it waits behind; a request granted at once
                // names none.
                if (!granted.Contains(request))
                {
                    foreach (Request holder in _Holders) request.BlockedBy.Add(holder.Describe());
                }
            }

            Complete(granted);
            if (granted.Contains(request))
            {
                ArmadaMetrics.RecordHostSlotWait(wait.Elapsed, true);
                return new Lease(request, vesselLease);
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
                vesselLease?.Dispose();
                ArmadaMetrics.RecordHostSlotWait(wait.Elapsed, false);
                throw;
            }

            ArmadaMetrics.RecordHostSlotWait(wait.Elapsed, true);
            return new Lease(request, vesselLease);
        }

        /// <summary>
        /// Mark the present moment of a lease, for a later <see cref="SharedSince"/> question.
        /// </summary>
        /// <param name="lease">A lease this lock granted.</param>
        /// <returns>The mark. A null lease, or one this lock did not grant, reads as 0.</returns>
        public static long ReadShareMark(IDisposable? lease)
        {
            if (lease is not Lease held) return 0;
            lock (_Sync)
            {
                return held.Request.ShareEvents;
            }
        }

        /// <summary>
        /// Whether another request held a slot at the same time as this lease at any moment after the mark.
        /// </summary>
        /// <param name="lease">A lease this lock granted.</param>
        /// <param name="mark">A mark read with <see cref="ReadShareMark"/> while the lease was held.</param>
        /// <returns>True when the lease shared the host after the mark. A null lease, or one this lock did not
        /// grant, never shared.</returns>
        public static bool SharedSince(IDisposable? lease, long mark)
        {
            if (lease is not Lease held) return false;
            lock (_Sync)
            {
                Request request = held.Request;
                if (_Holders.Count > 1 && _Holders.Contains(request)) return true;
                return request.ShareEvents > mark;
            }
        }

        /// <summary>
        /// How long a granted lease waited for its slot, why, and which holders were ahead of it.
        /// </summary>
        /// <param name="lease">A lease this lock granted.</param>
        /// <returns>The wait record, or null for a null lease or one this lock did not grant.</returns>
        public static HostSlotWaitRecord? ReadWaitRecord(IDisposable? lease)
        {
            if (lease is not Lease held) return null;
            lock (_Sync)
            {
                Request request = held.Request;
                HostSlotWaitRecord record = new HostSlotWaitRecord();
                record.VesselWaitMs = held.VesselWaitMs;
                record.HostWaitMs = ElapsedMs(request.RequestedUtc, request.GrantedUtc);
                record.MemoryHeldMs = request.MemoryHeldSinceUtc.HasValue ? ElapsedMs(request.MemoryHeldSinceUtc.Value, request.GrantedUtc) : 0;
                record.AloneHeldMs = request.AloneHeldSinceUtc.HasValue ? ElapsedMs(request.AloneHeldSinceUtc.Value, request.GrantedUtc) : 0;
                record.AheadAtRequest = request.AheadAtRequest;
                record.BlockedBy.AddRange(held.VesselBlockedBy);
                record.BlockedBy.AddRange(request.BlockedBy);
                return record;
            }
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
                snapshot.VesselSlotCount = _VesselSlotCount;
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

                foreach (KeyValuePair<string, VesselQueue> pair in _VesselQueues)
                {
                    int vesselPosition = 1;
                    foreach (Request waiter in pair.Value.Waiters)
                    {
                        snapshot.VesselWaiters.Add(new HostVesselWaiter
                        {
                            VesselKey = pair.Key,
                            Position = vesselPosition++,
                            Key = waiter.OwnerKey,
                            Description = waiter.OwnerDescription,
                            RequestedUtc = waiter.RequestedUtc
                        });
                    }
                }

                return snapshot;
            }
        }

        #endregion

        #region Private-Methods

        private static async Task<IDisposable> AcquireVesselAsync(
            string vesselKey,
            string? ownerKey,
            string? ownerDescription,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            LinkedListNode<Request>? node;
            Request request;
            List<Request> granted = new List<Request>();
            lock (_Sync)
            {
                VesselQueue queue = _VesselQueues.GetOrAdd(vesselKey, _ => new VesselQueue());
                request = new Request(ownerKey, ownerDescription, _Clock());
                node = queue.Waiters.AddLast(request);
                GrantVessel(queue, granted);
                if (!granted.Contains(request))
                {
                    foreach (Request holder in queue.Holders) request.BlockedBy.Add(holder.Describe());
                }
            }

            Complete(granted);
            if (granted.Contains(request)) return new VesselLease(vesselKey, request);

            try
            {
                using (token.Register(() => CancelVesselWaiting(vesselKey, node)))
                {
                    await request.Granted.Task.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            return new VesselLease(vesselKey, request);
        }

        private static void GrantVessel(VesselQueue queue, List<Request> granted)
        {
            while (queue.Holders.Count < _VesselSlotCount && queue.Waiters.First != null)
            {
                Request next = queue.Waiters.First.Value;
                queue.Waiters.RemoveFirst();
                queue.Holders.Add(next);
                next.GrantedUtc = _Clock();
                granted.Add(next);
            }
        }

        private static void CancelVesselWaiting(string vesselKey, LinkedListNode<Request>? node)
        {
            if (node == null) return;
            List<Request> granted = new List<Request>();
            lock (_Sync)
            {
                if (node.List == null) return;
                VesselQueue queue = _VesselQueues.GetOrAdd(vesselKey, _ => new VesselQueue());
                queue.Waiters.Remove(node);
                GrantVessel(queue, granted);
            }

            node.Value.Granted.TrySetCanceled();
            Complete(granted);
        }

        private static void ReleaseVessel(string vesselKey, Request request)
        {
            List<Request> granted = new List<Request>();
            lock (_Sync)
            {
                if (!_VesselQueues.TryGetValue(vesselKey, out VesselQueue? queue)) return;
                if (!queue.Holders.Remove(request)) return;
                GrantVessel(queue, granted);
            }

            Complete(granted);
        }

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

                // The holders that remain held their slots together with the released request until now.
                if (_Holders.Count > 0) CountShared();
                GrantWaiting(granted);
            }

            Complete(granted);
        }

        /// <summary>
        /// Move waiters to holders, first in line first, while a slot is free and memory admits the first
        /// request. A request held for memory ends the loop, so no later request passes it. A holder that holds
        /// the host alone ends the loop, and so does a first request that asks for the host alone while any slot
        /// is held. Call under <see cref="_Sync"/>; complete the returned requests outside it.
        /// </summary>
        private static void GrantWaiting(List<Request> granted)
        {
            _HeadHeldForMemory = false;
            while (_Holders.Count < _SlotCount && _Waiters.First != null)
            {
                Request next = _Waiters.First.Value;
                if (_Holders.Exists(holder => holder.Alone) || (next.Alone && _Holders.Count > 0))
                {
                    if (!next.AloneHeldSinceUtc.HasValue) next.AloneHeldSinceUtc = _Clock();
                    break;
                }

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
                if (_Holders.Count > 1) CountShared();
            }

            UpdateRecheckTimer(_HeadHeldForMemory);
        }

        /// <summary>
        /// Count one sharing event on every holder: the holders now hold slots together. Call under
        /// <see cref="_Sync"/>.
        /// </summary>
        private static void CountShared()
        {
            foreach (Request holder in _Holders) holder.ShareEvents++;
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

        /// <summary>
        /// Whole milliseconds from a start to an end, 0 when the end is unknown or earlier.
        /// </summary>
        private static long ElapsedMs(DateTime startUtc, DateTime? endUtc)
        {
            if (!endUtc.HasValue || endUtc.Value <= startUtc) return 0;
            return Convert.ToInt64(Math.Round((endUtc.Value - startUtc).TotalMilliseconds));
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

            /// <summary>True when the request holds the host alone: no other request is granted while it holds.</summary>
            public bool Alone { get; init; }

            /// <summary>How many times this request began or ended holding a slot together with another request.</summary>
            public long ShareEvents { get; set; }

            /// <summary>Requests ahead of this one in its queue when it joined.</summary>
            public int AheadAtRequest { get; init; }

            /// <summary>When this request, first in line, was first held by a request that holds the host alone.</summary>
            public DateTime? AloneHeldSinceUtc { get; set; }

            /// <summary>The holders this request waited behind, as "key: description".</summary>
            public List<string> BlockedBy { get; } = new List<string>();

            /// <summary>The holder as one "key: description" entry, safe inside a semicolon-separated line.</summary>
            public string Describe()
            {
                string text = (OwnerKey ?? "unnamed") + ": " + OwnerDescription;
                return text.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');
            }

            public TaskCompletionSource<bool> Granted { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class Lease : IDisposable
        {
            private readonly Request _Request;
            private readonly IDisposable? _VesselLease;
            private int _Released = 0;

            public Lease(Request request, IDisposable? vesselLease = null)
            {
                _Request = request ?? throw new ArgumentNullException(nameof(request));
                _VesselLease = vesselLease;
                if (vesselLease is VesselLease vessel)
                {
                    VesselWaitMs = ElapsedMs(vessel.Request.RequestedUtc, vessel.Request.GrantedUtc);
                    VesselBlockedBy = vessel.Request.BlockedBy;
                }
            }

            /// <summary>Milliseconds the request waited for its vessel's suite slot.</summary>
            public long VesselWaitMs { get; }

            /// <summary>The same-vessel holders the request waited behind.</summary>
            public List<string> VesselBlockedBy { get; } = new List<string>();

            public Request Request
            {
                get => _Request;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _Released, 1) == 0)
                {
                    // The host slot is released first and the vessel slot after it, the reverse of how they were
                    // taken, so the next of this vessel's suite commands enters the host queue only once this one
                    // is clear.
                    Release(_Request);
                    _VesselLease?.Dispose();
                }
            }
        }

        private sealed class VesselQueue
        {
            public LinkedList<Request> Waiters { get; } = new LinkedList<Request>();

            public List<Request> Holders { get; } = new List<Request>();
        }

        private sealed class VesselLease : IDisposable
        {
            private readonly string _VesselKey;
            private readonly Request _Request;
            private int _Released = 0;

            public VesselLease(string vesselKey, Request request)
            {
                _VesselKey = vesselKey;
                _Request = request;
            }

            public Request Request
            {
                get => _Request;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _Released, 1) == 0)
                {
                    ReleaseVessel(_VesselKey, _Request);
                }
            }
        }

        #endregion
    }
}
