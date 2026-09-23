namespace Armada.Core.Harbor
{
    using System;

    /// <summary>
    /// Outcome of a Harbor runner check that has no payload: accepted, or refused with a stable reason.
    /// </summary>
    public sealed class HarborRunnerCheck
    {
        #region Public-Members

        /// <summary>True when the check passed.</summary>
        public bool Accepted { get; }

        /// <summary>Stable refusal reason; empty when accepted.</summary>
        public string FailureReason { get; }

        #endregion

        #region Constructors-and-Factories

        private HarborRunnerCheck(bool accepted, string failureReason)
        {
            Accepted = accepted;
            FailureReason = failureReason;
        }

        /// <summary>A passed check.</summary>
        public static HarborRunnerCheck Pass { get; } = new HarborRunnerCheck(true, String.Empty);

        /// <summary>A refused check.</summary>
        /// <param name="failureReason">Stable refusal reason.</param>
        /// <returns>Check.</returns>
        public static HarborRunnerCheck Refuse(string failureReason)
        {
            if (String.IsNullOrWhiteSpace(failureReason)) throw new ArgumentNullException(nameof(failureReason));
            return new HarborRunnerCheck(false, failureReason);
        }

        #endregion
    }
}
