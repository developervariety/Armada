namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP tool arguments for sending a signal to a captain or a mission.
    /// </summary>
    public class SignalSendArgs
    {
        /// <summary>
        /// Target captain ID.
        /// </summary>
        public string CaptainId { get; set; } = "";

        /// <summary>
        /// Target mission ID. Mail to a mission goes to the mission's current captain.
        /// </summary>
        public string? MissionId { get; set; } = null;

        /// <summary>
        /// Signal message.
        /// </summary>
        public string Message { get; set; } = "";
    }
}
