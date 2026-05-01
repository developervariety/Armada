namespace Armada.Core.Recovery
{
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Pure router that maps a classified failure shape, triviality flag, and the
    /// owning mission's recovery-attempt count to one of the terminal
    /// <see cref="RecoveryAction"/> variants. Side-effect free -- the recovery
    /// handler is responsible for executing the chosen action.
    /// </summary>
    public sealed class RecoveryRouter : IRecoveryRouter
    {
        /// <summary>Default recovery cap; mirrored by ArmadaSettings.MaxRecoveryAttempts.</summary>
        public const int DefaultMaxAttempts = 3;

        private readonly int _MaxAttempts;

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="maxAttempts">Maximum recovery attempts before surfacing.
        /// Clamped to a non-negative value; 0 means no automated recovery.</param>
        public RecoveryRouter(int maxAttempts = DefaultMaxAttempts)
        {
            _MaxAttempts = maxAttempts < 0 ? 0 : maxAttempts;
        }

        /// <inheritdoc />
        public RecoveryAction Route(MergeFailureClassEnum failureClass, bool conflictTrivial, int recoveryAttempts)
        {
            if (recoveryAttempts >= _MaxAttempts)
            {
                return new RecoveryAction.Surface("recovery_exhausted");
            }

            switch (failureClass)
            {
                case MergeFailureClassEnum.StaleBase:
                    // Target advanced; redispatching off a fresh tip usually clears it.
                    return new RecoveryAction.Redispatch();

                case MergeFailureClassEnum.TextConflict:
                    // Trivial textual collisions can be resolved by a redispatched mission;
                    // non-trivial ones need the high-tier rebase-captain path.
                    return conflictTrivial
                        ? (RecoveryAction)new RecoveryAction.Redispatch()
                        : new RecoveryAction.RebaseCaptain();

                case MergeFailureClassEnum.TestFailureAfterMerge:
                    // Captain produced behaviorally-stale work -- rebase-captain re-runs
                    // it against the new tip with full conflict context.
                    return new RecoveryAction.RebaseCaptain();

                case MergeFailureClassEnum.TestFailureBeforeMerge:
                    // Captain produced broken work; this surfaces through Judge /
                    // NEEDS_REVISION rather than the auto-recovery loop.
                    return new RecoveryAction.Surface("test_failure_before_merge");

                case MergeFailureClassEnum.Unknown:
                default:
                    // Conservative: surface rather than guess.
                    return new RecoveryAction.Surface("classification_unknown");
            }
        }
    }
}
