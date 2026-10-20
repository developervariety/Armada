namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// The <c>handoff_outcome</c> hold. When enabled, a <c>partial</c> outcome gated at or above
    /// <see cref="Armada.Core.Services.HandoffOutcomeHold.MinimumConfidence"/> holds the next stage and
    /// lists the unmet acceptance criteria in the operator inbox, instead of Mailing them to the next
    /// stage. The operator releases the stage, Mails and releases it, or sends it to rescue. A hold no
    /// operator answers is released with the Mail after <see cref="TimeoutMinutes"/>. Off by default; the
    /// decision's own mode remains the kill switch for the decision itself.
    /// </summary>
    public class TypedDecisionHandoffHoldSettings
    {
        #region Public-Members

        /// <summary>
        /// Whether a confident <c>partial</c> outcome holds the next stage. Default false, which keeps the
        /// Mail-and-continue handoff.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Minutes a hold waits for an operator choice before it is released with the Mail. Default 240
        /// (four hours); clamped to 1 through 10,080 (one week).
        /// </summary>
        public int TimeoutMinutes
        {
            get => _TimeoutMinutes;
            set => _TimeoutMinutes = Math.Clamp(value, 1, 10080);
        }

        #endregion

        #region Private-Members

        private int _TimeoutMinutes = 240;

        #endregion

        #region Public-Methods

        /// <summary>
        /// A copy carrying the same values, so a hot reload never shares the file's instance.
        /// </summary>
        /// <returns>The copy.</returns>
        public TypedDecisionHandoffHoldSettings Clone()
        {
            return new TypedDecisionHandoffHoldSettings
            {
                Enabled = Enabled,
                TimeoutMinutes = TimeoutMinutes
            };
        }

        #endregion
    }
}
