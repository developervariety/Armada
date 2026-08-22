namespace Armada.Server.Mcp
{
    /// <summary>
    /// Arguments containing one structured check-run identifier.
    /// </summary>
    public class CheckRunIdArgs
    {
        /// <summary>
        /// Check-run identifier.
        /// </summary>
        public string CheckRunId { get; set; } = string.Empty;

        /// <summary>
        /// Return the complete command log rather than a bounded tail. Defaults to false, because
        /// a build or test log routinely exceeds the tool output limit and a caller that only
        /// wants the verdict should not have to pay for it.
        /// </summary>
        public bool IncludeOutput { get; set; } = false;

        /// <summary>
        /// How many trailing output lines to include when <see cref="IncludeOutput"/> is false.
        /// Null uses the tool default.
        /// </summary>
        public int? OutputTailLines { get; set; } = null;
    }
}
