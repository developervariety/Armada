namespace Armada.Core.Harbor
{
    using System;

    /// <summary>
    /// Immutable lease for one live Harbor runner connection. A reconnect receives a new generation and
    /// invalidates all work owned by the previous lease.
    /// </summary>
    public sealed class HarborRunnerSession
    {
        /// <summary>Verified runner identity.</summary>
        public HarborRunnerIdentity Identity { get; }

        /// <summary>Monotonic connection generation assigned by the registry.</summary>
        public long Generation { get; }

        /// <summary>UTC time at which this generation was registered.</summary>
        public DateTime ConnectedUtc { get; }

        internal HarborRunnerSession(HarborRunnerIdentity identity, long generation)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
            Generation = generation;
            ConnectedUtc = DateTime.UtcNow;
        }
    }
}
