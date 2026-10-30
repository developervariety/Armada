namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// How many of the automatic check runner's places hold a started check, published so the view of a
    /// pending check can say it waits for a place.
    /// </summary>
    /// <remarks>
    /// The runner starts at most a fixed number of checks at once, and a started check usually waits for
    /// the host command slot. A pending check the runner has not started has asked for nothing, so its
    /// view has no slot position to show; when every place is taken, this is why it waits.
    /// </remarks>
    public static class AutomaticCheckRunPlaces
    {
        #region Private-Members

        private static readonly object _Sync = new object();
        private static int _InFlight = 0;
        private static int _Limit = 0;
        private static Dictionary<string, string> _GateWaits = new Dictionary<string, string>(StringComparer.Ordinal);
        private static Dictionary<string, string> _WorkWaits = new Dictionary<string, string>(StringComparer.Ordinal);

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record how many places hold a started check and how many places the runner has.
        /// </summary>
        /// <param name="inFlight">Started checks that have not finished.</param>
        /// <param name="limit">Most checks the runner starts at once.</param>
        public static void Publish(int inFlight, int limit)
        {
            lock (_Sync)
            {
                _InFlight = Math.Max(0, inFlight);
                _Limit = Math.Max(0, limit);
            }
        }

        /// <summary>
        /// Record which pending checks the runner holds back behind a running definition-of-done gate.
        /// </summary>
        /// <param name="waits">Check run id to the mission whose gate it waits for.</param>
        public static void PublishGateWaits(IReadOnlyDictionary<string, string>? waits)
        {
            Dictionary<string, string> copy = new Dictionary<string, string>(StringComparer.Ordinal);
            if (waits != null)
            {
                foreach (KeyValuePair<string, string> pair in waits) copy[pair.Key] = pair.Value;
            }

            lock (_Sync)
            {
                _GateWaits = copy;
            }
        }

        /// <summary>
        /// Record which pending voyage checks the runner holds back because the work they measure is not final.
        /// </summary>
        /// <param name="waits">Check run id to the reason the voyage's work is not final yet.</param>
        public static void PublishWorkWaits(IReadOnlyDictionary<string, string>? waits)
        {
            Dictionary<string, string> copy = new Dictionary<string, string>(StringComparer.Ordinal);
            if (waits != null)
            {
                foreach (KeyValuePair<string, string> pair in waits) copy[pair.Key] = pair.Value;
            }

            lock (_Sync)
            {
                _WorkWaits = copy;
            }
        }

        /// <summary>
        /// Why a pending check the runner has not started still waits, or null when nothing holds it.
        /// </summary>
        /// <param name="checkRunId">The check run id.</param>
        /// <returns>The reason, or null.</returns>
        public static string? DescribeQueueReason(string? checkRunId)
        {
            lock (_Sync)
            {
                if (!String.IsNullOrWhiteSpace(checkRunId) && _GateWaits.TryGetValue(checkRunId, out string? missionId))
                {
                    return "waiting for the running definition-of-done gate of mission " + missionId
                        + ", which runs the same suite on this vessel; a gate that passes the same command on the same commit satisfies this check";
                }

                if (!String.IsNullOrWhiteSpace(checkRunId) && _WorkWaits.TryGetValue(checkRunId, out string? workWait))
                {
                    return workWait;
                }

                if (_Limit <= 0 || _InFlight < _Limit) return null;
                return "waiting for a place in the automatic check runner: " + _InFlight + " of " + _Limit
                    + " places hold started checks, and this check starts when one of them finishes";
            }
        }

        #endregion
    }
}
