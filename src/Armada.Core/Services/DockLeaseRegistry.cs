namespace Armada.Core.Services
{
    using System;
    using System.Collections.Concurrent;

    /// <summary>
    /// In-memory, reference-counted leases that pin a dock's worktree against reclamation while a
    /// long-running operation still needs it. The definition-of-done gate takes a lease for the
    /// whole evaluation - including its host-wide queue wait - so a gate queued behind another
    /// gate cannot lose its worktree to dock reclamation before it executes (obj_msg0hlkw).
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
            _Leases.AddOrUpdate(dockId, 0, (_, current) => Math.Max(0, current - 1));
            if (_Leases.TryGetValue(dockId, out int count) && count == 0)
            {
                _Leases.TryRemove(dockId, out _);
            }
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

        #region Private-Members

        private static readonly ConcurrentDictionary<string, int> _Leases =
            new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        #endregion
    }
}
