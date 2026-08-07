namespace Armada.Test.Shared.Infrastructure
{
    /// <summary>
    /// Stable tag constants applied to <c>TestCaseDescriptor.Tags</c> so runners can filter
    /// and so coverage can be audited for balance between positive and negative cases.
    /// Every behavioral case should carry either <see cref="Positive"/> or <see cref="Negative"/>.
    /// </summary>
    public static class TestTags
    {
        /// <summary>
        /// Happy-path case: valid input or expected-success behavior.
        /// </summary>
        public const string Positive = "positive";

        /// <summary>
        /// Failure-path case: invalid input, error handling, rejection, boundary, or fault injection.
        /// </summary>
        public const string Negative = "negative";

        /// <summary>
        /// Case that exercises reliability behavior (recovery, timeout, cancellation, leases, reconciliation).
        /// </summary>
        public const string Reliability = "reliability";

        /// <summary>
        /// Case that requires a real spawned subprocess.
        /// </summary>
        public const string Process = "process";

        /// <summary>
        /// Case that requires a live database.
        /// </summary>
        public const string Database = "database";

        /// <summary>
        /// Case that requires a running HTTP server (end-to-end).
        /// </summary>
        public const string EndToEnd = "e2e";
    }
}
