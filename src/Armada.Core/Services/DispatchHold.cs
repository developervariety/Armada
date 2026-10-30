namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database.Interfaces;

    /// <summary>
    /// A dispatch hold. When engaged, every voyage and mission dispatch
    /// through the admiral is rejected until the hold is cleared, so an operator
    /// working on Armada itself can stop new work before a rebuild or redeploy.
    /// With a store, <see cref="EngageAsync"/> and <see cref="ClearAsync"/> persist the hold and
    /// <see cref="RestoreAsync"/> reloads it at startup, so a restart keeps the hold.
    /// Without a store the hold lives in memory only.
    /// In-flight work is not held: later stages of a dispatched voyage are assigned and a stalled captain is
    /// relaunched in its dock while the hold is engaged.
    /// A vessel hold (<see cref="EngageVesselAsync"/>) applies the same refusal to one vessel only, and also refuses
    /// creating or approving a deployment of that vessel. It is persisted and restored the same way, and the
    /// fleet-wide hold and each vessel hold are stored, engaged and cleared independently.
    /// </summary>
    public class DispatchHold
    {
        #region Private-Members

        private readonly object _Lock = new object();
        private bool _Active = false;
        private string _Reason = String.Empty;
        private string? _SetBy = null;
        private DateTime _SetByUtc = DateTime.UtcNow;
        private readonly IDispatchHoldMethods? _Store;
        private readonly Dictionary<string, DispatchHoldSnapshot> _Vessels = new Dictionary<string, DispatchHoldSnapshot>(StringComparer.Ordinal);

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate a hold that lives in memory only.
        /// </summary>
        public DispatchHold()
        {
        }

        /// <summary>
        /// Instantiate a hold that persists through the store.
        /// </summary>
        /// <param name="store">Store that keeps the engaged hold across a restart.</param>
        public DispatchHold(IDispatchHoldMethods store)
        {
            _Store = store ?? throw new ArgumentNullException(nameof(store));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Engage the hold and persist it when a store is set. The hold is engaged in memory first, so a
        /// failed write still refuses dispatch; the write failure then reaches the caller.
        /// </summary>
        /// <param name="reason">Why the hold is engaged. Required.</param>
        /// <param name="setBy">Who engaged the hold.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task EngageAsync(string reason, string? setBy = null, CancellationToken token = default)
        {
            Engage(reason, setBy);
            if (_Store == null) return;
            DispatchHoldSnapshot? snapshot = Snapshot();
            if (snapshot != null) await _Store.SaveAsync(snapshot, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Clear the hold and remove it from the store when a store is set.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public async Task ClearAsync(CancellationToken token = default)
        {
            Clear();
            if (_Store != null) await _Store.ClearAsync(token).ConfigureAwait(false);
        }

        /// <summary>
        /// Reload the stored hold into memory. Startup calls it before any dispatch path runs.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The restored hold, or null when none was stored or no store is set.</returns>
        public async Task<DispatchHoldSnapshot?> RestoreAsync(CancellationToken token = default)
        {
            if (_Store == null) return null;
            List<DispatchHoldSnapshot> vessels = await _Store.ReadVesselsAsync(token).ConfigureAwait(false);
            lock (_Lock)
            {
                _Vessels.Clear();
                foreach (DispatchHoldSnapshot vessel in vessels) _Vessels[vessel.VesselId!] = vessel;
            }

            DispatchHoldSnapshot? stored = await _Store.ReadAsync(token).ConfigureAwait(false);
            if (stored == null) return null;
            lock (_Lock)
            {
                _Active = true;
                _Reason = stored.Reason;
                _SetBy = stored.SetBy;
                _SetByUtc = stored.SetByUtc;
            }
            return stored;
        }

        /// <summary>
        /// Engage the hold in memory. Reason is required; setBy names who engaged it. Use
        /// <see cref="EngageAsync"/> to persist the hold.
        /// </summary>
        public void Engage(string reason, string? setBy = null)
        {
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required to engage the dispatch hold.", nameof(reason));

            lock (_Lock)
            {
                _Active = true;
                _Reason = reason;
                _SetBy = setBy;
                _SetByUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Clear the hold in memory. Use <see cref="ClearAsync"/> to remove the stored hold.
        /// </summary>
        public void Clear()
        {
            lock (_Lock)
            {
                _Active = false;
                _Reason = String.Empty;
                _SetBy = null;
                _SetByUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Snapshot of the current hold state, or null when no hold is active.
        /// </summary>
        public DispatchHoldSnapshot? Snapshot()
        {
            lock (_Lock)
            {
                if (!_Active) return null;
                return new DispatchHoldSnapshot
                {
                    Reason = _Reason,
                    SetBy = _SetBy,
                    SetByUtc = _SetByUtc
                };
            }
        }

        /// <summary>
        /// Hold one vessel: dispatch and deploy for it are refused until the hold is cleared, while every other vessel
        /// is unaffected. Engaging again replaces the vessel's reason. Persisted when the hold has a store.
        /// </summary>
        /// <param name="vesselId">The vessel.</param>
        /// <param name="reason">Why the vessel is held.</param>
        /// <param name="setBy">Who set the hold.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The vessel hold.</returns>
        public async Task<DispatchHoldSnapshot> EngageVesselAsync(string vesselId, string reason, string? setBy = null, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(vesselId)) throw new ArgumentException("A vessel id is required to hold a vessel.", nameof(vesselId));
            if (String.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required to hold a vessel.", nameof(reason));
            DispatchHoldSnapshot hold = new DispatchHoldSnapshot { VesselId = vesselId, Reason = reason, SetBy = setBy, SetByUtc = DateTime.UtcNow };
            if (_Store != null) await _Store.SaveVesselAsync(hold, token).ConfigureAwait(false);
            lock (_Lock)
            {
                _Vessels[vesselId] = hold;
            }

            return hold;
        }

        /// <summary>
        /// Clear one vessel's hold. Persisted when the hold has a store.
        /// </summary>
        /// <param name="vesselId">The vessel.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The hold that was cleared, or null when the vessel was not held.</returns>
        public async Task<DispatchHoldSnapshot?> ClearVesselAsync(string vesselId, CancellationToken token = default)
        {
            if (String.IsNullOrWhiteSpace(vesselId)) throw new ArgumentException("A vessel id is required to clear a vessel hold.", nameof(vesselId));
            if (_Store != null) await _Store.ClearVesselAsync(vesselId, token).ConfigureAwait(false);
            lock (_Lock)
            {
                if (!_Vessels.TryGetValue(vesselId, out DispatchHoldSnapshot? previous)) return null;
                _Vessels.Remove(vesselId);
                return previous;
            }
        }

        /// <summary>
        /// The hold on one vessel, or null when it is not held.
        /// </summary>
        /// <param name="vesselId">The vessel.</param>
        /// <returns>The vessel hold, or null.</returns>
        public DispatchHoldSnapshot? VesselSnapshot(string? vesselId)
        {
            if (String.IsNullOrWhiteSpace(vesselId)) return null;
            lock (_Lock)
            {
                return _Vessels.TryGetValue(vesselId, out DispatchHoldSnapshot? hold) ? Copy(hold) : null;
            }
        }

        /// <summary>
        /// Every vessel hold, ordered by vessel id.
        /// </summary>
        /// <returns>The vessel holds.</returns>
        public List<DispatchHoldSnapshot> VesselHolds()
        {
            lock (_Lock)
            {
                return _Vessels.Values.OrderBy(hold => hold.VesselId, StringComparer.Ordinal).Select(Copy).ToList();
            }
        }

        /// <summary>
        /// Refuse a dispatch for a vessel: throws when the fleet-wide hold is engaged or the vessel is held.
        /// </summary>
        /// <param name="vesselId">The vessel the dispatch is for; null checks the fleet-wide hold only.</param>
        public void ThrowIfActive(string? vesselId)
        {
            ThrowIfActive();
            DispatchHoldSnapshot? hold = VesselSnapshot(vesselId);
            if (hold != null) throw new DispatchHoldActiveException(hold, RefusalMessage(hold));
        }

        /// <summary>
        /// The one dispatch-hold admission rule. Every path that creates a voyage or dispatches a
        /// mission calls this before it writes anything. Throws
        /// <see cref="DispatchHoldActiveException"/> (an InvalidOperationException) when the hold
        /// is active; the message carries the holder and reason so the caller can report why
        /// dispatch was refused.
        /// </summary>
        public void ThrowIfActive()
        {
            lock (_Lock)
            {
                if (!_Active) return;
                DispatchHoldSnapshot snapshot = new DispatchHoldSnapshot
                {
                    Reason = _Reason,
                    SetBy = _SetBy,
                    SetByUtc = _SetByUtc
                };
                throw new DispatchHoldActiveException(snapshot, RefusalMessage(snapshot));
            }
        }

        /// <summary>
        /// The message that reports a hold restored at startup, naming the reason and who set it.
        /// </summary>
        /// <param name="hold">The restored hold.</param>
        /// <returns>The message text.</returns>
        public static string RestoredMessage(DispatchHoldSnapshot hold)
        {
            if (hold == null) throw new ArgumentNullException(nameof(hold));
            string holder = String.IsNullOrWhiteSpace(hold.SetBy) ? "unknown" : hold.SetBy!;
            return "Dispatch hold restored after an admiral restart. Set by " + holder + " at " +
                hold.SetByUtc.ToString("u") + ": " + hold.Reason + " Dispatching stays paused until the hold is cleared.";
        }

        /// <summary>
        /// The operator-facing refusal message for a held dispatch. It starts with
        /// <see cref="DispatchHoldActiveException.ReasonCode"/>, so a surface that relays only the
        /// message still carries the named reason.
        /// </summary>
        /// <param name="hold">The engaged hold.</param>
        /// <returns>The refusal message naming the code, holder, time and reason.</returns>
        public static string RefusalMessage(DispatchHoldSnapshot hold)
        {
            if (hold == null) throw new ArgumentNullException(nameof(hold));
            string holder = String.IsNullOrWhiteSpace(hold.SetBy) ? "unknown" : hold.SetBy!;
            if (!String.IsNullOrWhiteSpace(hold.VesselId))
            {
                return DispatchHoldActiveException.VesselReasonCode + ": Vessel " + hold.VesselId + " is held since "
                    + hold.SetByUtc.ToString("u") + " (set by " + holder + "): " + hold.Reason
                    + " Nothing was accepted or created for this vessel; other vessels are not held."
                    + " Dispatch or deploy again once the vessel hold is cleared (armada_vessel_hold action=clear).";
            }
            return DispatchHoldActiveException.ReasonCode + ": Dispatch hold active since " + hold.SetByUtc.ToString("u") +
                " (set by " + holder + "): " + hold.Reason +
                " Nothing was accepted or created. Dispatch again once the hold is cleared (armada_dispatch_hold action=clear after Armada is redeployed).";
        }

        #endregion

        #region Private-Methods

        private static DispatchHoldSnapshot Copy(DispatchHoldSnapshot hold)
        {
            return new DispatchHoldSnapshot { VesselId = hold.VesselId, Reason = hold.Reason, SetBy = hold.SetBy, SetByUtc = hold.SetByUtc };
        }

        #endregion
    }
}
