namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// A runtime dispatch hold. When engaged, every voyage and mission dispatch
    /// through the admiral is rejected until the hold is cleared, so an operator
    /// working on Armada itself can stop new work before a rebuild or redeploy.
    /// Runtime state only: a restart clears the hold.
    /// </summary>
    public class DispatchHold
    {
        #region Private-Members

        private readonly object _Lock = new object();
        private bool _Active = false;
        private string _Reason = String.Empty;
        private string? _SetBy = null;
        private DateTime _SetByUtc = DateTime.UtcNow;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Engage the hold. Reason is required; setBy names who engaged it.
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
        /// Clear the hold.
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
            return DispatchHoldActiveException.ReasonCode + ": Dispatch hold active since " + hold.SetByUtc.ToString("u") +
                " (set by " + holder + "): " + hold.Reason +
                " Nothing was accepted or created. Dispatch again once the hold is cleared (armada_dispatch_hold action=clear after Armada is redeployed).";
        }

        #endregion
    }
}
