namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// The latest definition-of-done gate result of a mission.
    /// </summary>
    public sealed class TraceGateResult
    {
        #region Public-Members

        /// <summary>Gate outcome.</summary>
        public string? Outcome { get; set; } = null;

        /// <summary>Failure class when the gate failed.</summary>
        public string? FailureClass { get; set; } = null;

        /// <summary>Exit code of the gate command.</summary>
        public int? ExitCode { get; set; } = null;

        /// <summary>Commit the gate ran on.</summary>
        public string? CommitSha { get; set; } = null;

        /// <summary>Names of failing tests, bounded.</summary>
        public List<string> FailedTests { get; set; } = new List<string>();

        /// <summary>True when more tests failed than are named.</summary>
        public bool FailedTestsOverflow { get; set; } = false;

        #endregion
    }
}
