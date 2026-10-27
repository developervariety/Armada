namespace Armada.Server.Mcp
{
    /// <summary>
    /// Arguments of list_battery_packs.
    /// </summary>
    public class BatteryPackListArgs
    {
        /// <summary>
        /// Page number (default 1).
        /// </summary>
        public int? PageNumber { get; set; } = null;

        /// <summary>
        /// Page size (default 100).
        /// </summary>
        public int? PageSize { get; set; } = null;
    }
}
