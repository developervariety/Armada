namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Circuit-breaker state for one model endpoint base URL, shared by every captain that uses it. Consecutive
    /// endpoint-level failures (transient errors, model unavailable) open the breaker for a short window, during which
    /// captains on that endpoint are not given new missions; a clean run closes it.
    /// </summary>
    public class EndpointBreakerState
    {
        #region Public-Members

        /// <summary>
        /// Endpoint-level failures since the last clean run.
        /// </summary>
        public int ConsecutiveFailures { get; set; } = 0;

        /// <summary>
        /// When the breaker closes again (UTC), or null while it is closed.
        /// </summary>
        public DateTime? OpenUntilUtc { get; set; } = null;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        public EndpointBreakerState()
        {
        }

        #endregion
    }
}
