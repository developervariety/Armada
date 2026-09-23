namespace Armada.Core.Harbor
{
    using System;

    /// <summary>
    /// Outcome of registering a typed pending request on a Harbor runner session: the accepted request, or a stable
    /// refusal reason.
    /// </summary>
    /// <typeparam name="T">Response type.</typeparam>
    public sealed class HarborPendingRegistration<T>
    {
        #region Public-Members

        /// <summary>Accepted pending request; null when refused.</summary>
        public HarborPendingRequest<T>? Pending { get; }

        /// <summary>True when the request was registered.</summary>
        public bool Accepted => Pending != null;

        /// <summary>Stable refusal reason; empty when accepted.</summary>
        public string FailureReason { get; }

        #endregion

        #region Constructors-and-Factories

        private HarborPendingRegistration(HarborPendingRequest<T>? pending, string failureReason)
        {
            Pending = pending;
            FailureReason = failureReason;
        }

        /// <summary>An accepted pending request.</summary>
        /// <param name="pending">Registered pending request.</param>
        /// <returns>Registration.</returns>
        public static HarborPendingRegistration<T> Accept(HarborPendingRequest<T> pending)
        {
            return new HarborPendingRegistration<T>(pending ?? throw new ArgumentNullException(nameof(pending)), String.Empty);
        }

        /// <summary>A refused pending request.</summary>
        /// <param name="failureReason">Stable refusal reason.</param>
        /// <returns>Registration.</returns>
        public static HarborPendingRegistration<T> Refuse(string failureReason)
        {
            if (String.IsNullOrWhiteSpace(failureReason)) throw new ArgumentNullException(nameof(failureReason));
            return new HarborPendingRegistration<T>(null, failureReason);
        }

        #endregion
    }
}
