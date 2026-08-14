namespace Armada.Core.Database.Interfaces
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Database operations for durable coordination leases. These provide restart-safe,
    /// multi-instance-safe mutual exclusion via atomic compare-and-swap on a lease name, with
    /// TTL-based takeover so a crashed holder never blocks progress permanently. Used to replace
    /// in-memory coordination (process-exit dedup, merge-queue processing guard, per-repository
    /// provisioning locks).
    /// </summary>
    public interface ICoordinationLeaseMethods
    {
        /// <summary>
        /// Attempt to acquire the named lease for the given holder. Succeeds atomically when the
        /// lease does not exist or has expired, setting the holder and an expiry of now + ttl.
        /// Returns false if another holder currently owns a non-expired lease.
        /// </summary>
        /// <param name="name">Unique lease name.</param>
        /// <param name="holder">Holder identifier claiming the lease.</param>
        /// <param name="ttl">How long the lease is held before it may be taken over.</param>
        /// <param name="tenantId">Optional tenant scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the lease was acquired.</returns>
        Task<bool> TryAcquireAsync(string name, string holder, TimeSpan ttl, string? tenantId = null, CancellationToken token = default);

        /// <summary>
        /// Extend the expiry of a lease the caller already holds. Succeeds only when the current
        /// holder matches and the lease has not expired. Returns false otherwise.
        /// </summary>
        /// <param name="name">Lease name.</param>
        /// <param name="holder">Holder identifier that must match the current owner.</param>
        /// <param name="ttl">New time-to-live from now.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the lease was renewed.</returns>
        Task<bool> TryRenewAsync(string name, string holder, TimeSpan ttl, CancellationToken token = default);

        /// <summary>
        /// Release a lease held by the given holder. No-op when the lease is absent or owned by a
        /// different holder, so a stale holder cannot release a lease it no longer owns.
        /// </summary>
        /// <param name="name">Lease name.</param>
        /// <param name="holder">Holder identifier that must match the current owner.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        Task ReleaseAsync(string name, string holder, CancellationToken token = default);

        /// <summary>
        /// Read the current lease record by name, or null when it does not exist.
        /// </summary>
        /// <param name="name">Lease name.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The lease, or null.</returns>
        Task<CoordinationLease?> ReadAsync(string name, CancellationToken token = default);

        /// <summary>
        /// Delete all expired leases. Housekeeping to keep the table small; correctness does not
        /// depend on it because acquisition already treats an expired lease as free.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of expired leases removed.</returns>
        Task<int> PurgeExpiredAsync(CancellationToken token = default);
    }
}
