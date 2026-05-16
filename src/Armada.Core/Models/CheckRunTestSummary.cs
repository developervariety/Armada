namespace Armada.Core.Models
{
    /// <summary>
    /// Structured test execution summary extracted from command output.
    /// </summary>
    public class CheckRunTestSummary
    {
        /// <summary>
        /// Parser/format name.
        /// </summary>
        public string? Format { get; set; } = null;

        /// <summary>
        /// Total test count when known.
        /// </summary>
        public int? Total { get; set; } = null;

        /// <summary>
        /// Passed test count when known.
        /// </summary>
        public int? Passed { get; set; } = null;

        /// <summary>
        /// Failed or error test count when known.
        /// </summary>
        public int? Failed { get; set; } = null;

        /// <summary>
        /// Skipped test count when known.
        /// </summary>
        public int? Skipped { get; set; } = null;

        /// <summary>
        /// Parsed test duration in milliseconds when known.
        /// </summary>
        public long? DurationMs { get; set; } = null;
    }
}
