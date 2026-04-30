namespace Armada.Core.Services.Interfaces
{
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Pure router that maps a classified failure shape, triviality flag, and the
    /// owning mission's recovery-attempt count to one of the terminal
    /// <see cref="RecoveryAction"/> variants. No side effects.
    /// </summary>
    public interface IRecoveryRouter
    {
        /// <summary>
        /// Selects the terminal recovery action.
        /// </summary>
        /// <param name="failureClass">Classifier output.</param>
        /// <param name="conflictTrivial">True when the conflict is small enough that a
        /// blind redispatch is likely to succeed; false otherwise.</param>
        /// <param name="recoveryAttempts">Current mission recovery-attempt counter.</param>
        /// <returns>The terminal recovery action to execute.</returns>
        RecoveryAction Route(MergeFailureClassEnum failureClass, bool conflictTrivial, int recoveryAttempts);
    }
}
