namespace Armada.Core.Models
{
    using Armada.Core.Enums;

    /// <summary>
    /// Lightweight mission record carrying only the fields needed by the scheduler hot path:
    /// identifier, title (for broad-scope detection), and status.
    /// Returned by IMissionMethods.GetActiveVesselSummariesAsync to avoid hydrating
    /// description, diff_snapshot, agent_output, and playbook columns during assignment checks.
    /// </summary>
    public sealed class ActiveMissionSummary
    {
        #region Public-Members

        /// <summary>
        /// Mission identifier.
        /// </summary>
        public string Id { get; set; } = "";

        /// <summary>
        /// Mission title (used for broad-scope title matching).
        /// </summary>
        public string Title { get; set; } = "";

        /// <summary>
        /// Current mission status.
        /// </summary>
        public MissionStatusEnum Status { get; set; }

        #endregion
    }
}
