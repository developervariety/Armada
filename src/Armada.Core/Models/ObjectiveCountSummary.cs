namespace Armada.Core.Models
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// Objective counts by lifecycle status and by backlog state, taken from one read of the objectives a
    /// caller can see. A value with no objectives has no entry.
    /// </summary>
    public class ObjectiveCountSummary
    {
        #region Public-Members

        /// <summary>
        /// Objective count per lifecycle status.
        /// </summary>
        public Dictionary<ObjectiveStatusEnum, int> ByStatus { get; set; } = new Dictionary<ObjectiveStatusEnum, int>();

        /// <summary>
        /// Objective count per backlog state.
        /// </summary>
        public Dictionary<ObjectiveBacklogStateEnum, int> ByBacklogState { get; set; } = new Dictionary<ObjectiveBacklogStateEnum, int>();

        #endregion
    }
}
