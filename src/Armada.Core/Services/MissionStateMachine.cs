namespace Armada.Core.Services
{
    using Armada.Core.Enums;

    /// <summary>
    /// Single authoritative definition of the mission lifecycle: which status transitions are legal
    /// and how statuses are classified. Previously the transition table and the terminal/post-work/
    /// active status sets were duplicated across several services and an agent handler, which drifted
    /// out of sync. Centralizing them here removes that drift and gives one place to reason about and
    /// test the state machine.
    /// </summary>
    public static class MissionStateMachine
    {
        /// <summary>
        /// Whether a transition from one status to another is legal. Terminal statuses
        /// (Complete, Failed, Cancelled) permit no further transitions.
        /// </summary>
        /// <param name="current">Current status.</param>
        /// <param name="target">Proposed next status.</param>
        /// <returns>True if the transition is allowed.</returns>
        public static bool IsValidTransition(MissionStatusEnum current, MissionStatusEnum target)
        {
            switch (current)
            {
                case MissionStatusEnum.Pending:
                    return target == MissionStatusEnum.Assigned
                        || target == MissionStatusEnum.Cancelled;

                case MissionStatusEnum.Assigned:
                    return target == MissionStatusEnum.InProgress
                        || target == MissionStatusEnum.Cancelled;

                case MissionStatusEnum.InProgress:
                    return target == MissionStatusEnum.WorkProduced
                        || target == MissionStatusEnum.Testing
                        || target == MissionStatusEnum.Review
                        || target == MissionStatusEnum.Complete
                        || target == MissionStatusEnum.Failed
                        || target == MissionStatusEnum.Cancelled;

                case MissionStatusEnum.WorkProduced:
                    // Failed is reached when the voyage ended Failed and the produced work never
                    // landed; see TerminalVoyageMissionRule.
                    return target == MissionStatusEnum.PullRequestOpen
                        || target == MissionStatusEnum.Complete
                        || target == MissionStatusEnum.LandingFailed
                        || target == MissionStatusEnum.Failed
                        || target == MissionStatusEnum.Cancelled;

                case MissionStatusEnum.PullRequestOpen:
                    return target == MissionStatusEnum.Complete
                        || target == MissionStatusEnum.LandingFailed
                        || target == MissionStatusEnum.Cancelled;

                case MissionStatusEnum.Testing:
                    return target == MissionStatusEnum.Review
                        || target == MissionStatusEnum.InProgress
                        || target == MissionStatusEnum.Complete
                        || target == MissionStatusEnum.Failed;

                case MissionStatusEnum.Review:
                    return target == MissionStatusEnum.Complete
                        || target == MissionStatusEnum.InProgress
                        || target == MissionStatusEnum.Failed;

                case MissionStatusEnum.LandingFailed:
                    return target == MissionStatusEnum.WorkProduced
                        || target == MissionStatusEnum.Failed
                        || target == MissionStatusEnum.Cancelled;

                default:
                    // Complete, Failed, Cancelled are terminal.
                    return false;
            }
        }

        /// <summary>
        /// Whether a captain may move its own mission to <paramref name="target"/> by emitting an
        /// <c>[ARMADA:STATUS]</c> marker. Agent output may report progress only: the target must be
        /// InProgress, Testing, or Review, and the transition must be legal in
        /// <see cref="IsValidTransition"/>. Post-work and terminal statuses (WorkProduced, Complete,
        /// Failed, Cancelled, PullRequestOpen, LandingFailed) are reached only through the completion
        /// and landing paths, which run their checks; a status marker naming one is ignored.
        /// </summary>
        /// <param name="current">Current status.</param>
        /// <param name="target">Status the agent output requested.</param>
        /// <returns>True if the agent output may apply the transition.</returns>
        public static bool IsAgentReportableTransition(MissionStatusEnum current, MissionStatusEnum target)
        {
            bool progressTarget = target == MissionStatusEnum.InProgress
                || target == MissionStatusEnum.Testing
                || target == MissionStatusEnum.Review;
            return progressTarget && IsValidTransition(current, target);
        }

        /// <summary>
        /// Terminal statuses: the mission is finished and will not transition again.
        /// </summary>
        /// <param name="status">Status to classify.</param>
        /// <returns>True if terminal.</returns>
        public static bool IsTerminal(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Complete
                || status == MissionStatusEnum.Failed
                || status == MissionStatusEnum.Cancelled;
        }

        /// <summary>
        /// Statuses at or past the point where the agent has produced work: terminal statuses plus
        /// WorkProduced, PullRequestOpen, and LandingFailed. Used by completion idempotency guards
        /// to avoid re-processing a mission whose work already exists.
        /// </summary>
        /// <param name="status">Status to classify.</param>
        /// <returns>True if the mission is terminal or in a post-work state.</returns>
        public static bool IsTerminalOrPostWork(MissionStatusEnum status)
        {
            return IsTerminal(status)
                || status == MissionStatusEnum.WorkProduced
                || status == MissionStatusEnum.PullRequestOpen
                || status == MissionStatusEnum.LandingFailed;
        }

        /// <summary>
        /// Whether cancelling a mission's voyage also cancels the mission: the transition to
        /// Cancelled is legal and the mission has not produced work (Pending, Assigned, InProgress).
        /// A finished mission keeps its outcome, and produced work under an ended voyage takes its
        /// status from landing evidence through <see cref="TerminalVoyageMissionRule"/>.
        /// </summary>
        /// <param name="status">Mission status.</param>
        /// <returns>True if voyage cancellation cancels a mission in this status.</returns>
        public static bool IsCancelledWithVoyage(MissionStatusEnum status)
        {
            return IsValidTransition(status, MissionStatusEnum.Cancelled)
                && !IsTerminalOrPostWork(status);
        }

        /// <summary>
        /// Statuses in which a captain is actively responsible for the mission
        /// (InProgress, Assigned, Review, Testing).
        /// </summary>
        /// <param name="status">Status to classify.</param>
        /// <returns>True if the mission is actively in progress.</returns>
        public static bool IsActive(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.InProgress
                || status == MissionStatusEnum.Assigned
                || status == MissionStatusEnum.Review
                || status == MissionStatusEnum.Testing;
        }
    }
}
