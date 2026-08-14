namespace Armada.Core.Services
{
    using Armada.Core.Enums;

    /// <summary>
    /// The valid status transitions for a background job, kept pure so the rules are unit-testable and the
    /// service, coordinator, and any future worker all agree. A queued job may start running or be
    /// cancelled; a running job may succeed, fail, or be cancelled; terminal statuses do not transition.
    /// </summary>
    public static class JobStateMachine
    {
        #region Public-Methods

        /// <summary>
        /// Whether a status is terminal (no further transitions).
        /// </summary>
        /// <param name="status">The status to test.</param>
        /// <returns>True when terminal.</returns>
        public static bool IsTerminal(JobStatusEnum status)
        {
            return status == JobStatusEnum.Succeeded
                || status == JobStatusEnum.Failed
                || status == JobStatusEnum.Cancelled;
        }

        /// <summary>
        /// Whether a transition from one status to another is allowed.
        /// </summary>
        /// <param name="from">Current status.</param>
        /// <param name="to">Proposed next status.</param>
        /// <returns>True when the transition is valid.</returns>
        public static bool CanTransition(JobStatusEnum from, JobStatusEnum to)
        {
            if (from == to) return false;
            if (IsTerminal(from)) return false;

            switch (from)
            {
                case JobStatusEnum.Queued:
                    return to == JobStatusEnum.Running || to == JobStatusEnum.Cancelled;
                case JobStatusEnum.Running:
                    return to == JobStatusEnum.Succeeded || to == JobStatusEnum.Failed || to == JobStatusEnum.Cancelled;
                default:
                    return false;
            }
        }

        #endregion
    }
}
