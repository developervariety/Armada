namespace Armada.Core.Models
{
    /// <summary>One exact branch terminal in a durable Architect integration barrier.</summary>
    public sealed class MissionIntegrationBarrierMember
    {
        /// <summary>Stable branch key from the accepted Architect plan.</summary>
        public string BranchKey { get; set; } = string.Empty;

        /// <summary>Root mission created for this branch.</summary>
        public string RootMissionId { get; set; } = string.Empty;

        /// <summary>Terminal mission accepted when the plan was accepted.</summary>
        public string AcceptedTerminalMissionId { get; set; } = string.Empty;

        /// <summary>Current terminal mission, which can change only through an accepted rescue rewire.</summary>
        public string CurrentTerminalMissionId { get; set; } = string.Empty;
    }
}
