namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// The result of a check run.
    /// </summary>
    public sealed class TraceCheckResult
    {
        #region Public-Members

        /// <summary>Check type.</summary>
        public string? Type { get; set; } = null;

        /// <summary>Exit code.</summary>
        public int? ExitCode { get; set; } = null;

        /// <summary>Total tests when known.</summary>
        public int? Total { get; set; } = null;

        /// <summary>Passed tests when known.</summary>
        public int? Passed { get; set; } = null;

        /// <summary>Failed tests when known.</summary>
        public int? Failed { get; set; } = null;

        /// <summary>Skipped tests when known.</summary>
        public int? Skipped { get; set; } = null;

        /// <summary>Summary, redacted and bounded.</summary>
        public string? Summary { get; set; } = null;

        #endregion
    }
}
