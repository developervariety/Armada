namespace Armada.Server.Mcp
{
    using System;

    /// <summary>
    /// Arguments of the <c>armada_trace_backfill</c> tool.
    /// </summary>
    public class TraceBackfillArgs
    {
        /// <summary>
        /// Oldest creation time a table's backfill starts from when it has never run. Ignored once that
        /// table's cursor exists. Null uses the tool default.
        /// </summary>
        public DateTime? SinceUtc { get; set; } = null;

        /// <summary>
        /// Most source rows one call visits. Null uses the tool default.
        /// </summary>
        public int? MaxRows { get; set; } = null;
    }
}
