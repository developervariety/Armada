namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using Armada.Core.Enums;

    /// <summary>
    /// The one registry of agent processes the admiral stopped on purpose.
    /// </summary>
    /// <remarks>
    /// A stopped process still exits, and its exit code (often a kill signal) says nothing about the
    /// work. Whoever stops a process on purpose registers it here before the stop, and every exit
    /// handler asks this registry before it reads the exit code, so an intentional stop is never
    /// mistaken for a crash, an out-of-memory kill, or the outcome of the process that replaced it.
    /// A record names the captain and mission as well as the process id, so a reused process id
    /// never matches. Runtime state only: a restart clears it.
    /// </remarks>
    public sealed class IntentionalProcessStops
    {
        #region Private-Members

        private readonly ConcurrentDictionary<int, StopRecord> _Stops = new ConcurrentDictionary<int, StopRecord>();

        #endregion

        #region Public-Members

        /// <summary>
        /// Number of registered stops whose exit has not been handled yet.
        /// </summary>
        public int Count => _Stops.Count;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register a process as stopped on purpose. Call before the stop so its exit cannot arrive first.
        /// </summary>
        /// <param name="processId">Process identifier.</param>
        /// <param name="captainId">Captain that owns the process.</param>
        /// <param name="missionId">Mission the process was running.</param>
        /// <param name="kind">What the exit means.</param>
        /// <returns>True when this call registered the stop; false when the process was already registered.</returns>
        public bool TryRegister(int processId, string captainId, string missionId, IntentionalStopKindEnum kind)
        {
            if (String.IsNullOrEmpty(captainId)) throw new ArgumentNullException(nameof(captainId));
            if (String.IsNullOrEmpty(missionId)) throw new ArgumentNullException(nameof(missionId));
            return _Stops.TryAdd(processId, new StopRecord(captainId, missionId, kind));
        }

        /// <summary>
        /// Consume the registered stop for an exiting process when it matches the captain, mission and kind.
        /// A record of another kind is left for the handler that owns that kind.
        /// </summary>
        /// <param name="processId">Exiting process identifier.</param>
        /// <param name="captainId">Captain the exit was reported for.</param>
        /// <param name="missionId">Mission the exit was reported for.</param>
        /// <param name="kind">Kind the caller handles.</param>
        /// <returns>True when a matching stop was registered and is now consumed.</returns>
        public bool TryTake(int processId, string captainId, string missionId, IntentionalStopKindEnum kind)
        {
            if (!_Stops.TryGetValue(processId, out StopRecord? record)) return false;
            if (!Matches(record, captainId, missionId, kind)) return false;
            return _Stops.TryRemove(new System.Collections.Generic.KeyValuePair<int, StopRecord>(processId, record));
        }

        /// <summary>
        /// Withdraw a registration whose stop did not happen.
        /// </summary>
        /// <param name="processId">Process identifier.</param>
        public void Forget(int processId)
        {
            _Stops.TryRemove(processId, out _);
        }

        #endregion

        #region Private-Methods

        private static bool Matches(StopRecord record, string captainId, string missionId, IntentionalStopKindEnum kind)
        {
            return record.Kind == kind
                && String.Equals(record.CaptainId, captainId, StringComparison.Ordinal)
                && String.Equals(record.MissionId, missionId, StringComparison.Ordinal);
        }

        #endregion

        #region Private-Classes

        private sealed class StopRecord
        {
            public StopRecord(string captainId, string missionId, IntentionalStopKindEnum kind)
            {
                CaptainId = captainId;
                MissionId = missionId;
                Kind = kind;
            }

            public string CaptainId { get; }

            public string MissionId { get; }

            public IntentionalStopKindEnum Kind { get; }
        }

        #endregion
    }
}
