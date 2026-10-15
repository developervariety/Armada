namespace Test.Shared.Suites.Services
{
    using System;

    /// <summary>
    /// A UTC clock a test moves by hand, for the Harbor connection manager's liveness and grace periods. Thread safe.
    /// </summary>
    public sealed class HarborTestClock
    {
        #region Private-Members

        private readonly object _Lock = new object();
        private DateTime _Now;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate at a starting time.
        /// </summary>
        /// <param name="start">Starting time (UTC).</param>
        public HarborTestClock(DateTime start)
        {
            _Now = start;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// The current time.
        /// </summary>
        /// <returns>The time (UTC).</returns>
        public DateTime Now()
        {
            lock (_Lock) return _Now;
        }

        /// <summary>
        /// Move the clock forward.
        /// </summary>
        /// <param name="by">How far.</param>
        public void Advance(TimeSpan by)
        {
            lock (_Lock) _Now = _Now.Add(by);
        }

        #endregion
    }
}
