namespace Armada.Core.Recovery
{
    /// <summary>
    /// Pure router mapping a classified failure plus the mission's recovery
    /// budget to a terminal <see cref="RecoveryAction"/>. No I/O; no side
    /// effects; the full decision table is covered by unit tests in M3.
    /// M1 ships the interface only; the implementation lands in M3.
    /// </summary>
    public interface IRecoveryRouter
    {
        /// <summary>
        /// Decide which recovery action to take.
        /// </summary>
        /// <param name="cls">
        /// Failure class produced by the classifier.
        /// </param>
        /// <param name="conflictTrivial">
        /// True when the conflict is judged "trivial" (small file count and
        /// small diff line count -- exact thresholds live with the router
        /// implementation, not the interface).
        /// </param>
        /// <param name="recoveryAttempts">
        /// Current value of <see cref="Armada.Core.Models.Mission.RecoveryAttempts"/>
        /// for the failing mission. The router enforces the bound (cap of 2)
        /// by returning <see cref="RecoveryAction.Surface"/>.
        /// </param>
        /// <returns>The terminal action to execute.</returns>
        RecoveryAction Route(
            MergeFailureClass cls,
            bool conflictTrivial,
            int recoveryAttempts);
    }
}
