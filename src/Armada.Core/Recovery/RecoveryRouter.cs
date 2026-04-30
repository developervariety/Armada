namespace Armada.Core.Recovery
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Pure implementation of <see cref="IRecoveryRouter"/>: maps a classified failure
    /// shape, triviality flag, and the owning mission's recovery-attempt counter to one
    /// of the terminal <see cref="RecoveryAction"/> variants. Side-effect free.
    /// </summary>
    public sealed class RecoveryRouter : IRecoveryRouter
    {
        private const string _Header = "[RecoveryRouter] ";
        private const string _RecoveryExhaustedReason = "recovery_exhausted";
        private const string _TestFailureBeforeMergeReason = "test_failure_before_merge";
        private const string _ClassifierUnknownReason = "classifier_unknown";

        private readonly int _MaxRecoveryAttempts;

        /// <summary>
        /// Instantiate the router with a static cap pulled from <see cref="ArmadaSettings.MaxRecoveryAttempts"/>.
        /// </summary>
        /// <param name="settings">Application settings (required).</param>
        public RecoveryRouter(ArmadaSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _MaxRecoveryAttempts = settings.MaxRecoveryAttempts;
        }

        /// <inheritdoc />
        public RecoveryAction Route(MergeFailureClassEnum failureClass, bool conflictTrivial, int recoveryAttempts)
        {
            switch (failureClass)
            {
                case MergeFailureClassEnum.StaleBase:
                    return recoveryAttempts < _MaxRecoveryAttempts
                        ? new RecoveryAction.Redispatch()
                        : new RecoveryAction.Surface(_RecoveryExhaustedReason);

                case MergeFailureClassEnum.TextConflict:
                    if (recoveryAttempts >= _MaxRecoveryAttempts)
                        return new RecoveryAction.Surface(_RecoveryExhaustedReason);
                    return conflictTrivial
                        ? (RecoveryAction)new RecoveryAction.Redispatch()
                        : new RecoveryAction.RebaseCaptain();

                case MergeFailureClassEnum.TestFailureAfterMerge:
                    return recoveryAttempts < _MaxRecoveryAttempts
                        ? new RecoveryAction.RebaseCaptain()
                        : new RecoveryAction.Surface(_RecoveryExhaustedReason);

                case MergeFailureClassEnum.TestFailureBeforeMerge:
                    return new RecoveryAction.Surface(_TestFailureBeforeMergeReason);

                case MergeFailureClassEnum.Unknown:
                    return new RecoveryAction.Surface(_ClassifierUnknownReason);

                default:
                    return new RecoveryAction.Surface(_ClassifierUnknownReason);
            }
        }
    }
}
