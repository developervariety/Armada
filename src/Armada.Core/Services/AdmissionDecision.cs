namespace Armada.Core.Services
{
    /// <summary>
    /// The outcome of a resource-pressure admission check: whether a captain may launch now, and if not,
    /// a human-readable reason the mission was deferred.
    /// </summary>
    public sealed class AdmissionDecision
    {
        #region Public-Members

        /// <summary>
        /// True when the host has enough headroom to launch a captain; false to defer the mission.
        /// </summary>
        public bool Admit { get; }

        /// <summary>
        /// When <see cref="Admit"/> is false, why the launch was deferred; null when admitted.
        /// </summary>
        public string? DeferReason { get; }

        #endregion

        #region Constructors-and-Factories

        private AdmissionDecision(bool admit, string? deferReason)
        {
            Admit = admit;
            DeferReason = deferReason;
        }

        /// <summary>
        /// An admit decision.
        /// </summary>
        /// <returns>A decision permitting launch.</returns>
        public static AdmissionDecision Admitted()
        {
            return new AdmissionDecision(true, null);
        }

        /// <summary>
        /// A defer decision carrying the reason.
        /// </summary>
        /// <param name="reason">Why the launch was deferred.</param>
        /// <returns>A decision deferring launch.</returns>
        public static AdmissionDecision Deferred(string reason)
        {
            return new AdmissionDecision(false, reason);
        }

        #endregion
    }
}
