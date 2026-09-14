namespace Armada.Server
{
    /// <summary>
    /// Outcome of an operator mission status transition request.
    /// </summary>
    public enum MissionStatusTransitionOutcomeEnum
    {
        /// <summary>
        /// The transition, landing, or pipeline handoff was applied.
        /// </summary>
        Applied,

        /// <summary>
        /// The requested target is not a valid transition from the current status.
        /// </summary>
        InvalidTransition,

        /// <summary>
        /// A manual completion gate refused the request; the mission was not changed.
        /// </summary>
        Refused,

        /// <summary>
        /// The mission no longer exists after the landing pipeline ran.
        /// </summary>
        MissionMissing
    }
}
