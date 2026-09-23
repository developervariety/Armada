namespace Armada.Core.Harbor
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// Result of resolving a Harbor runner's durable owner: the owner and enrollment generation, or a stable
    /// failure reason.
    /// </summary>
    public sealed class HarborRunnerOwnerResolution
    {
        #region Public-Members

        /// <summary>True when the runner's enrollment and principal are valid.</summary>
        public bool Resolved { get; }

        /// <summary>Current owner when resolved; otherwise null.</summary>
        public AuthContext? Owner { get; }

        /// <summary>Durable enrollment generation when resolved, zero when the resolver does not track generations or on failure.</summary>
        public long Generation { get; }

        /// <summary>Stable failure reason, for example <c>runner_enrollment_revoked</c>; empty when resolved.</summary>
        public string FailureReason { get; }

        #endregion

        #region Constructors-and-Factories

        private HarborRunnerOwnerResolution(bool resolved, AuthContext? owner, long generation, string failureReason)
        {
            Resolved = resolved;
            Owner = owner;
            Generation = generation;
            FailureReason = failureReason;
        }

        /// <summary>A resolved owner.</summary>
        /// <param name="owner">Current owner.</param>
        /// <param name="generation">Durable enrollment generation, or zero when not tracked.</param>
        /// <returns>Resolution.</returns>
        public static HarborRunnerOwnerResolution Success(AuthContext owner, long generation)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            return new HarborRunnerOwnerResolution(true, owner, generation, String.Empty);
        }

        /// <summary>A failed resolution with its stable reason.</summary>
        /// <param name="failureReason">Stable failure reason.</param>
        /// <returns>Resolution.</returns>
        public static HarborRunnerOwnerResolution Failure(string failureReason)
        {
            if (String.IsNullOrWhiteSpace(failureReason)) throw new ArgumentNullException(nameof(failureReason));
            return new HarborRunnerOwnerResolution(false, null, 0, failureReason);
        }

        #endregion
    }
}
