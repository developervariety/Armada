namespace Armada.Core.Models
{
    /// <summary>
    /// One active mission relevant to a workspace.
    /// </summary>
    public class WorkspaceActiveMission
    {
        /// <summary>
        /// Mission identifier.
        /// </summary>
        public string MissionId { get; set; } = string.Empty;

        /// <summary>
        /// Mission title.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Current mission status.
        /// </summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// Repository-relative scoped files inferred from the mission description.
        /// </summary>
        public List<string> ScopedFiles { get; set; } = new List<string>();
    }
}
