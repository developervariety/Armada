namespace Armada.Server.Mcp
{
    /// <summary>
    /// Arguments of get_battery_run.
    /// </summary>
    public class BatteryRunIdArgs
    {
        /// <summary>
        /// Run (bat_ prefix).
        /// </summary>
        public string? Id { get; set; } = null;
    }
}
