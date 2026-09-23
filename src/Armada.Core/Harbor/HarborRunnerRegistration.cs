namespace Armada.Core.Harbor
{
    using System;

    /// <summary>
    /// Outcome of registering a Harbor runner session: the accepted session lease, or a stable refusal reason.
    /// </summary>
    public sealed class HarborRunnerRegistration
    {
        #region Public-Members

        /// <summary>Accepted session lease; null when refused.</summary>
        public HarborRunnerSession? Session { get; }

        /// <summary>True when a session was registered.</summary>
        public bool Accepted => Session != null;

        /// <summary>Stable refusal reason; empty when accepted.</summary>
        public string FailureReason { get; }

        #endregion

        #region Constructors-and-Factories

        private HarborRunnerRegistration(HarborRunnerSession? session, string failureReason)
        {
            Session = session;
            FailureReason = failureReason;
        }

        /// <summary>An accepted registration.</summary>
        /// <param name="session">Registered session lease.</param>
        /// <returns>Registration.</returns>
        public static HarborRunnerRegistration Accept(HarborRunnerSession session)
        {
            return new HarborRunnerRegistration(session ?? throw new ArgumentNullException(nameof(session)), String.Empty);
        }

        /// <summary>A refused registration.</summary>
        /// <param name="failureReason">Stable refusal reason.</param>
        /// <returns>Registration.</returns>
        public static HarborRunnerRegistration Refuse(string failureReason)
        {
            if (String.IsNullOrWhiteSpace(failureReason)) throw new ArgumentNullException(nameof(failureReason));
            return new HarborRunnerRegistration(null, failureReason);
        }

        #endregion
    }
}
