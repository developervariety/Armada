namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Raised when another dispatch attempt holds an objective's admission lease for longer than
    /// the bounded admission wait. The dispatch did not start and created nothing; the caller may
    /// retry after <see cref="RetryAfter"/>.
    /// </summary>
    public class ObjectiveDispatchBusyException : InvalidOperationException
    {
        /// <summary>
        /// The objective whose admission was busy.
        /// </summary>
        public string ObjectiveId { get; }

        /// <summary>
        /// Suggested delay before a retry, derived from the current holder's lease expiry.
        /// </summary>
        public TimeSpan RetryAfter { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="objectiveId">The objective whose admission was busy.</param>
        /// <param name="retryAfter">Suggested delay before a retry.</param>
        public ObjectiveDispatchBusyException(string objectiveId, TimeSpan retryAfter)
            : base("Objective " + objectiveId + " is being dispatched by another request; retry after "
                + Math.Ceiling(retryAfter.TotalSeconds) + " second(s).")
        {
            ObjectiveId = objectiveId;
            RetryAfter = retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
        }
    }
}
