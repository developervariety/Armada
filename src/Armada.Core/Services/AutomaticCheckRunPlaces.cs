namespace Armada.Core.Services
{
    using System;

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
        /// Why a pending check the runner has not started still waits, or null when a place is free or no
        /// runner has reported.
        /// </summary>
        /// <returns>The reason, or null.</returns>
        public static string? DescribeQueueReason()
        {
            lock (_Sync)
            {
                if (_Limit <= 0 || _InFlight < _Limit) return null;
                return "waiting for a place in the automatic check runner: " + _InFlight + " of " + _Limit
                    + " places hold started checks, and this check starts when one of them finishes";
            }
        }

        #endregion
    }
}
