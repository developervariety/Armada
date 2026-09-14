namespace Armada.Core.Harbor
{
    using System;

    /// <summary>Publishes durable enrollment generation changes to live Harbor registries.</summary>
    public interface IHarborRunnerOwnerChangeNotifier
    {
        /// <summary>
        /// Raised after an enrollment is created, re-enrolled, or revoked. Subscribers update local
        /// leases only and do not perform database I/O while holding their own locks.
        /// </summary>
        event Action<string, long>? OwnerChanged;
    }
}
