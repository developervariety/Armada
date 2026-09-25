namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP tool arguments for armada_mission_status.
    /// </summary>
    public class MissionStatusArgs : MissionIdArgs
    {
        /// <summary>
        /// Return the mission's stored description (its full brief text). Off by default, because a
        /// description can run to tens of kilobytes.
        /// </summary>
        public bool IncludeDescription { get; set; } = false;
    }
}
