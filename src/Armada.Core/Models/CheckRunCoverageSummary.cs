namespace Armada.Core.Models
{
    /// <summary>
    /// Structured coverage summary extracted from a coverage artifact or command output.
    /// </summary>
    public class CheckRunCoverageSummary
    {
        /// <summary>
        /// Coverage parser/format name.
        /// </summary>
        public string? Format { get; set; } = null;

        /// <summary>
        /// Relative artifact path or logical source for the parsed coverage.
        /// </summary>
        public string? SourcePath { get; set; } = null;

        /// <summary>
        /// Line coverage when available.
        /// </summary>
        public CheckRunCoverageMetric? Lines { get; set; } = null;

        /// <summary>
        /// Branch coverage when available.
        /// </summary>
        public CheckRunCoverageMetric? Branches { get; set; } = null;

        /// <summary>
        /// Function or method coverage when available.
        /// </summary>
        public CheckRunCoverageMetric? Functions { get; set; } = null;

        /// <summary>
        /// Statement or instruction coverage when available.
        /// </summary>
        public CheckRunCoverageMetric? Statements { get; set; } = null;
    }
}
