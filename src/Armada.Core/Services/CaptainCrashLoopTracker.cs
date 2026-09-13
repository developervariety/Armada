namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using Armada.Core.Settings;

    /// <summary>
    /// Tracks distinct runtime crash failures for each captain in a bounded in-memory window.
    /// State intentionally resets when the admiral restarts; persisted quarantine state remains
    /// authoritative across restarts.
    /// </summary>
    public sealed class CaptainCrashLoopTracker
    {
        #region Public-Methods

        /// <summary>
        /// Records one crash failure and returns whether the configured threshold is reached.
        /// A repeated failure identifier is ignored, so duplicate process-exit notifications do
        /// not advance the counter.
        /// </summary>
        public bool Record(string captainId, string failureId, DateTime nowUtc, out int count)
        {
            return Record(captainId, failureId, nowUtc, out count, out _);
        }

        /// <summary>Records a crash and returns a generation token for conditional consumption.</summary>
        public bool Record(string captainId, string failureId, DateTime nowUtc, out int count, out long generation)
        {
            if (String.IsNullOrWhiteSpace(captainId)) throw new ArgumentException("Captain id is required.", nameof(captainId));
            if (String.IsNullOrWhiteSpace(failureId)) throw new ArgumentException("Failure id is required.", nameof(failureId));

            lock (_StateGate)
            {
                CrashLoopState state = _States.GetOrAdd(captainId, _ => new CrashLoopState());
                DateTime cutoffUtc = nowUtc.ToUniversalTime().AddMinutes(-_Settings.WindowMinutes);
                lock (state)
                {
                    state.LastTouchedUtc = nowUtc.ToUniversalTime();
                    List<string> expiredIds = new List<string>();
                    foreach (KeyValuePair<string, DateTime> existing in state.FailureTimes)
                    {
                        if (existing.Value < cutoffUtc) expiredIds.Add(existing.Key);
                    }
                    foreach (string expiredId in expiredIds)
                    {
                        state.FailureTimes.Remove(expiredId);
                    }

                    if (state.FailureTimes.ContainsKey(failureId))
                    {
                        count = state.FailureTimes.Count;
                        generation = state.Generation;
                        return count >= _Settings.FailureThreshold;
                    }

                    while (state.FailureTimes.Count >= _MaximumFailuresPerCaptain)
                    {
                        string? oldestId = null;
                        DateTime oldestUtc = DateTime.MaxValue;
                        foreach (KeyValuePair<string, DateTime> existing in state.FailureTimes)
                        {
                            if (existing.Value < oldestUtc)
                            {
                                oldestId = existing.Key;
                                oldestUtc = existing.Value;
                            }
                        }
                        if (oldestId == null) break;
                        state.FailureTimes.Remove(oldestId);
                    }

                    state.FailureTimes.Add(failureId, nowUtc.ToUniversalTime());
                    state.Generation = ++_NextGeneration;
                    count = state.FailureTimes.Count;
                    generation = state.Generation;
                    EvictOldestStatesIfNeeded();
                    return count >= _Settings.FailureThreshold;
                }
            }
        }

        /// <summary>
        /// Returns the number of captain histories currently retained in memory.
        /// </summary>
        public int TrackedCaptainCount => _States.Count;

        /// <summary>
        /// Clears the captain's in-memory crash history after quarantine or an accepted reset.
        /// </summary>
        public void Reset(string captainId)
        {
            if (String.IsNullOrWhiteSpace(captainId)) throw new ArgumentException("Captain id is required.", nameof(captainId));
            lock (_StateGate)
            {
                _States.TryRemove(captainId, out _);
            }
        }

        /// <summary>Clears history only when no later crash was recorded.</summary>
        public bool ResetIfGeneration(string captainId, long generation)
        {
            if (String.IsNullOrWhiteSpace(captainId)) throw new ArgumentException("Captain id is required.", nameof(captainId));
            lock (_StateGate)
            {
                if (!_States.TryGetValue(captainId, out CrashLoopState? state)) return false;
                lock (state)
                {
                    if (state.Generation != generation) return false;
                    _States.TryRemove(captainId, out _);
                    return true;
                }
            }
        }

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Creates a tracker with the supplied crash-loop policy.
        /// </summary>
        public CaptainCrashLoopTracker(CrashLoopDetectionSettings settings)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion

        #region Private-Members

        private sealed class CrashLoopState
        {
            public Dictionary<string, DateTime> FailureTimes { get; } = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            public DateTime LastTouchedUtc { get; set; }
            public long Generation { get; set; }
        }

        private readonly CrashLoopDetectionSettings _Settings;
        private readonly ConcurrentDictionary<string, CrashLoopState> _States = new ConcurrentDictionary<string, CrashLoopState>(StringComparer.Ordinal);
        private readonly object _StateGate = new object();
        private const int _MaximumTrackedCaptains = 1024;
        private const int _MaximumFailuresPerCaptain = 256;
        private long _NextGeneration;

        private void EvictOldestStatesIfNeeded()
        {
            while (_States.Count > _MaximumTrackedCaptains)
            {
                KeyValuePair<string, CrashLoopState>? oldest = null;
                foreach (KeyValuePair<string, CrashLoopState> candidate in _States)
                {
                    if (oldest == null || candidate.Value.LastTouchedUtc < oldest.Value.Value.LastTouchedUtc)
                    {
                        oldest = candidate;
                    }
                }

                if (oldest == null || !_States.TryRemove(oldest.Value.Key, out _)) return;
            }
        }

        #endregion
    }
}
