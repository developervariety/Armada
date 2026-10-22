namespace Armada.Server.Mcp
{
    /// <summary>
    /// Arguments of the <c>armada_trace</c> tool.
    /// </summary>
    public class TraceArgs
    {
        /// <summary>
        /// Full or short id of an objective, voyage, mission, check run, incident, merge entry or deployment.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Most records returned. Null uses the tool default.
        /// </summary>
        public int? MaxRecords { get; set; } = null;

        /// <summary>
        /// Return the final message of every record, not only of the requested one. Defaults to false.
        /// </summary>
        public bool IncludeMessages { get; set; } = false;
    }
}
