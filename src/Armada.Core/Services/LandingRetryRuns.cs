namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;

    /// <summary>
    /// In-process registry of running landing retries, keyed by mission. A retry resets its mission to
    /// WorkProduced before it lands, and WorkProduced counts as done for voyage completion, so every reader
    /// that decides a voyage or drains its produced work asks this registry first: while a retry runs, the
    /// mission's outcome is not known and its work is already being landed.
    /// <para>
    /// The registry lives in process memory by design: a retry exists only inside the process running it.
    /// </para>
    /// </summary>
    public static class LandingRetryRuns
    {
        #region Private-Members

        private static readonly ConcurrentDictionary<string, byte> _Runs = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Register a retry for a mission. Call <see cref="End"/> when it finishes.
        /// </summary>
        /// <param name="missionId">Mission identifier.</param>
        /// <returns>False when a retry for the mission is already running.</returns>
        public static bool TryStart(string missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId)) throw new ArgumentNullException(nameof(missionId));
            return _Runs.TryAdd(missionId, 0);
        }

        /// <summary>
        /// Remove a mission's retry from the registry.
        /// </summary>
        /// <param name="missionId">Mission identifier; null or empty removes nothing.</param>
        public static void End(string? missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId)) return;
            _Runs.TryRemove(missionId, out byte _);
        }

        /// <summary>
        /// True while a landing retry for the mission runs.
        /// </summary>
        /// <param name="missionId">Mission identifier; may be null.</param>
        /// <returns>True when a retry is registered.</returns>
        public static bool IsRunning(string? missionId)
        {
            if (String.IsNullOrWhiteSpace(missionId)) return false;
            return _Runs.ContainsKey(missionId);
        }

        #endregion
    }
}
