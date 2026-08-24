namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// The one definition of when the autonomy layer may clear a scheduler pause it did not set.
    /// A pause is a safety control: a deploying session pauses so a container restart does not
    /// land on live voyages, and that reason outlives the session for as long as the deploy runs.
    /// So the rule is narrow. The pause must name its owner; the owner must be absent from the
    /// coordination presence window; and the absence must exceed the configured threshold. A pause
    /// with no owner recorded is an operator's to clear, never the autonomy layer's. Nothing here
    /// ever engages a pause, and the dispatch hold is out of scope: it clears itself on a successful
    /// redeploy, so a hold that survives is a deploy that stopped halfway and needs a human.
    /// </summary>
    public static class StalePauseRule
    {
        #region Public-Methods

        /// <summary>
        /// Decide whether a pause may be cleared as stale.
        /// </summary>
        /// <param name="paused">Whether the scheduler is paused.</param>
        /// <param name="pausedBy">Participant key that set the pause, or null when unattributed.</param>
        /// <param name="pausedUtc">UTC time the pause was set, or null when unattributed.</param>
        /// <param name="ownerLastSeenUtc">
        /// The owner's last coordination heartbeat, or null when the owner has never been seen on the
        /// board. An owner never seen is measured from the pause time itself.
        /// </param>
        /// <param name="nowUtc">The current UTC time.</param>
        /// <param name="absenceMinutes">Minutes of absence required before the pause is stale.</param>
        /// <returns>The decision with its reason and the measured absence.</returns>
        public static StalePauseDecision Evaluate(
            bool paused,
            string? pausedBy,
            DateTime? pausedUtc,
            DateTime? ownerLastSeenUtc,
            DateTime nowUtc,
            int absenceMinutes)
        {
            if (!paused)
            {
                return new StalePauseDecision(false, null, "The scheduler is not paused; there is nothing to clear.");
            }

            if (String.IsNullOrWhiteSpace(pausedBy) || !pausedUtc.HasValue)
            {
                return new StalePauseDecision(false, null, "The pause records no owner, so its reason cannot be judged stale. An operator must clear it.");
            }

            DateTime lastSignal = ownerLastSeenUtc.HasValue && ownerLastSeenUtc.Value > pausedUtc.Value
                ? ownerLastSeenUtc.Value
                : pausedUtc.Value;
            TimeSpan absence = nowUtc - lastSignal;
            if (absence < TimeSpan.Zero) absence = TimeSpan.Zero;

            if (absence <= TimeSpan.FromMinutes(absenceMinutes))
            {
                return new StalePauseDecision(
                    false,
                    absence,
                    "The pausing session " + pausedBy + " was last seen " + FormatMinutes(absence)
                    + " ago, inside the " + absenceMinutes + "-minute absence threshold. Its pause stands.");
            }

            return new StalePauseDecision(
                true,
                absence,
                "The pausing session " + pausedBy + " has been absent " + FormatMinutes(absence)
                + ", longer than the " + absenceMinutes + "-minute threshold. The pause is stale.");
        }

        #endregion

        #region Private-Methods

        private static string FormatMinutes(TimeSpan span)
        {
            return ((int)Math.Floor(span.TotalMinutes)).ToString() + " minutes";
        }

        #endregion
    }

    /// <summary>
    /// Result of <see cref="StalePauseRule.Evaluate"/>.
    /// </summary>
    public sealed class StalePauseDecision
    {
        /// <summary>
        /// True when the pause may be cleared.
        /// </summary>
        public bool CanClear { get; }

        /// <summary>
        /// How long the owner has been silent, or null when it could not be measured.
        /// </summary>
        public TimeSpan? MeasuredAbsence { get; }

        /// <summary>
        /// Plain-language reason, suitable for a board note.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="canClear">Whether the pause may be cleared.</param>
        /// <param name="measuredAbsence">Measured absence, or null.</param>
        /// <param name="reason">Reason text.</param>
        public StalePauseDecision(bool canClear, TimeSpan? measuredAbsence, string reason)
        {
            CanClear = canClear;
            MeasuredAbsence = measuredAbsence;
            Reason = reason ?? String.Empty;
        }
    }
}
