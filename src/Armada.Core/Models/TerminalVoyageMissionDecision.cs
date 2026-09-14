namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Outcome of the terminal-voyage mission rule for one mission.
    /// </summary>
    public sealed class TerminalVoyageMissionDecision
    {
        #region Public-Members

        /// <summary>
        /// Status the mission moves to, or null when the mission is left unchanged.
        /// </summary>
        public MissionStatusEnum? TargetStatus { get; }

        /// <summary>
        /// Machine-readable reason code for the change or for leaving the mission unchanged.
        /// </summary>
        public string Reason { get; }

        #endregion

        #region Constructors-and-Factories

        private TerminalVoyageMissionDecision(MissionStatusEnum? targetStatus, string reason)
        {
            TargetStatus = targetStatus;
            Reason = reason;
        }

        /// <summary>
        /// A decision that moves the mission to a terminal status.
        /// </summary>
        /// <param name="target">Terminal status.</param>
        /// <param name="reason">Reason code.</param>
        /// <returns>Decision.</returns>
        public static TerminalVoyageMissionDecision Move(MissionStatusEnum target, string reason)
        {
            return new TerminalVoyageMissionDecision(target, reason);
        }

        /// <summary>
        /// A decision that leaves the mission unchanged.
        /// </summary>
        /// <param name="reason">Reason code.</param>
        /// <returns>Decision.</returns>
        public static TerminalVoyageMissionDecision Keep(string reason)
        {
            return new TerminalVoyageMissionDecision(null, reason);
        }

        #endregion
    }
}
