namespace Armada.Test.Common
{
    /// <summary>
    /// Data class holding the result of a single test execution.
    /// </summary>
    public class TestResult
    {
        #region Public-Members

        /// <summary>
        /// Name of the test.
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Whether the test passed.
        /// </summary>
        public bool Passed { get; set; } = false;

        /// <summary>
        /// Optional message describing the result.
        /// </summary>
        public string? Message { get; set; } = null;

        /// <summary>
        /// Exception that caused the failure, if any.
        /// </summary>
        public Exception? Exception { get; set; } = null;

        /// <summary>
        /// Elapsed time in milliseconds.
        /// </summary>
        public long ElapsedMs { get; set; } = 0;

        /// <summary>Full owning suite type.</summary>
        public string SuiteId { get; set; } = "";

        /// <summary>Explicit reason for a named, unexecuted case.</summary>
        public string? SkipReason { get; set; }


        /// <summary>Repository-relative declaration path, without host directory prefixes.</summary>
        public string SourcePath { get; set; } = "";

        /// <summary>Source line containing the case declaration.</summary>
        public int SourceLine { get; set; }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Mark this test result as passed with the given elapsed time.
        /// </summary>
        public void MarkPassed(long elapsedMs)
        {
            Passed = true;
            ElapsedMs = elapsedMs;
        }

        /// <summary>
        /// Mark this test result as failed with the given elapsed time, message, and optional exception.
        /// </summary>
        public void MarkFailed(long elapsedMs, string message, Exception? exception = null)
        {
            Passed = false;
            ElapsedMs = elapsedMs;
            Message = message;
            Exception = exception;
        }

        #endregion
    }
}
