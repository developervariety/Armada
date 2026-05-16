namespace Armada.Core.Models
{
    /// <summary>
    /// Structured coverage metric for a single dimension such as lines or branches.
    /// </summary>
    public class CheckRunCoverageMetric
    {
        /// <summary>
        /// Covered item count when known.
        /// </summary>
        public int? Covered { get; set; } = null;

        /// <summary>
        /// Total item count when known.
        /// </summary>
        public int? Total { get; set; } = null;

        /// <summary>
        /// Percentage from 0 to 100 when known.
        /// </summary>
        public double? Percentage { get; set; } = null;
    }
}
