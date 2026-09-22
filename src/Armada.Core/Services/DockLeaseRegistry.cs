namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;

    /// <summary>
    /// In-memory, reference-counted leases that pin a dock's worktree against reclamation while a
    /// long-running operation still needs it. The definition-of-done gate takes a lease for the
    /// whole evaluation - including its host-wide queue wait - so a gate queued behind another
    /// gate cannot lose its worktree to dock reclamation before it executes.
    /// <para>
    /// The disk-lifecycle orphan sweep and <see cref="DockService.ReclaimAsync"/> both consult
    /// the registry and defer a dock whose lease is held. Leases live in process memory by
    /// design: a crash kills the operation holding the lease, so the dock becomes reclaimable
    /// again, which is the correct recovery outcome.
    /// </para>
    /// </summary>
    public static class DockLeaseRegistry
    {
        #region Public-Methods

        /// <summary>
        /// Take one lease on a dock. Reference-counted: the same dock may be leased by more than
        /// one operation, and the entry is removed only when the last lease is released.
        /// </summary>
        /// <param name="dockId">Dock identifier (dck_ prefix).</param>
        public static void Acquire(string dockId)
        {
            if (String.IsNullOrWhiteSpace(dockId)) return;
            _Leases.AddOrUpdate(dockId, 1, (_, current) => current + 1);
        }

        /// <summary>
        /// Release one lease on a dock. Releases are bounded at zero; the entry is removed when
        /// the count reaches zero so <see cref="IsHeld"/> stops protecting the dock.
        /// </summary>
        /// <param name="dockId">Dock identifier (dck_ prefix).</param>
        public static void Release(string dockId)
        {
            if (String.IsNullOrWhiteSpace(dockId)) return;
            int remaining = _Leases.AddOrUpdate(dockId, 0, (_, current) => Math.Max(0, current - 1));
            if (remaining != 0) return;

            BeforeZeroLeaseRemoval?.Invoke(dockId);

            // Remove only the entry this release observed at zero. An acquire that lands after the
            // decrement has already raised the count, so the conditional removal leaves its lease
            // in place instead of dropping a live lease by key.
            _Leases.TryRemove(new KeyValuePair<string, int>(dockId, 0));
        }

        /// <summary>
        /// True when at least one lease is held on the dock. Null or empty input is never held.
        /// </summary>
        /// <param name="dockId">Dock identifier (dck_ prefix); may be null.</param>
        /// <returns>True when the dock is leased.</returns>
        public static bool IsHeld(string? dockId)
        {
            if (String.IsNullOrWhiteSpace(dockId)) return false;
            return _Leases.ContainsKey(dockId);
        }

        /// <summary>Number of leases currently held on the dock, for tests and diagnostics.</summary>
        /// <param name="dockId">Dock identifier (dck_ prefix); may be null.</param>
        /// <returns>The lease count, or zero when the dock is not leased.</returns>
        public static int LeaseCount(string? dockId)
        {
            if (String.IsNullOrWhiteSpace(dockId)) return 0;
            if (_Leases.TryGetValue(dockId, out int count)) return count;
            return 0;
        }

        #endregion

        #region Internal-Members

        /// <summary>
        /// Test seam invoked by <see cref="Release"/> after it observes a zero count and before it
        /// removes the entry, so a test can interleave an acquire at exactly that point.
        /// </summary>
        internal static Action<string>? BeforeZeroLeaseRemoval { get; set; }

        #endregion

        #region Private-Members

        private static readonly ConcurrentDictionary<string, int> _Leases =
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        #endregion
    }
}
