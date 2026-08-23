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
        /// Throw InvalidOperationException when the hold is active. The message
        /// carries the holder and reason so the caller can report why dispatch
        /// was refused.
        /// </summary>
        public void ThrowIfActive()
        {
            lock (_Lock)
            {
                if (!_Active) return;
                string holder = String.IsNullOrWhiteSpace(_SetBy) ? "unknown" : _SetBy!;
                throw new InvalidOperationException(
                    "Dispatch hold active since " + _SetByUtc.ToString("u") +
                    " (set by " + holder + "): " + _Reason +
                    " Clear the hold with armada_dispatch_hold action=clear once Armada is redeployed.");
            }
        }

        #endregion
    }
}
