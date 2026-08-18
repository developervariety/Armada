namespace Armada.Core.Services
{
    /// <summary>
    /// The outcome of an auto-land evaluation: whether a passing mission may land unattended, and when it
    /// must hold, the reason.
    /// </summary>
    public sealed class AutoLandDecision
    {
        #region Public-Members

        /// <summary>
        /// True when the change satisfies the vessel's auto-land rules and may land without review.
        /// </summary>
        public bool Land { get; }

        /// <summary>
        /// When <see cref="Land"/> is false, why the mission is being held for review; null when landing.
        /// </summary>
        public string? HoldReason { get; }

        #endregion

        #region Constructors-and-Factories

        private AutoLandDecision(bool land, string? holdReason)
        {
            Land = land;
            HoldReason = holdReason;
        }

        /// <summary>
        /// A decision to land unattended.
        /// </summary>
        /// <returns>A landing decision.</returns>
        public static AutoLandDecision Lands()
        {
            return new AutoLandDecision(true, null);
        }

        /// <summary>
        /// A decision to hold for review, carrying the reason.
        /// </summary>
        /// <param name="reason">Why the mission is held.</param>
        /// <returns>A hold decision.</returns>
        public static AutoLandDecision Holds(string reason)
        {
            return new AutoLandDecision(false, reason);
        }

        #endregion
    }
}
