namespace Armada.Core.Services
{
    using System;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The one definition of how an objective's lifecycle status constrains its backlog state.
    /// Every objective write and read in <see cref="ObjectiveService"/> applies it, so manual
    /// edits, imports, link paths, recovery links and scheduler reconciliation cannot leave a
    /// terminal objective in a dispatchable backlog state.
    /// </summary>
    public static class ObjectiveLifecycleRules
    {
        #region Public-Members

        /// <summary>
        /// The backlog state a terminal objective rests in.
        /// </summary>
        public const ObjectiveBacklogStateEnum TerminalBacklogState = ObjectiveBacklogStateEnum.Inbox;

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when the status ends the objective's work: Completed or Cancelled.
        /// </summary>
        /// <param name="status">Objective status.</param>
        /// <returns>True for a terminal status.</returns>
        public static bool IsTerminalStatus(ObjectiveStatusEnum status)
        {
            return status == ObjectiveStatusEnum.Completed || status == ObjectiveStatusEnum.Cancelled;
        }

        /// <summary>
        /// Move a terminal objective out of any active backlog state into
        /// <see cref="TerminalBacklogState"/>. Nonterminal objectives are left unchanged.
        /// </summary>
        /// <param name="objective">Objective to normalize in place.</param>
        /// <returns>True when the backlog state was changed.</returns>
        public static bool ApplyTerminalBacklogState(Objective objective)
        {
            if (objective == null) throw new ArgumentNullException(nameof(objective));
            if (!IsTerminalStatus(objective.Status)) return false;
            if (objective.BacklogState == TerminalBacklogState) return false;

            objective.BacklogState = TerminalBacklogState;
            return true;
        }

        #endregion
    }
}
