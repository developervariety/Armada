namespace Armada.Core.Harbor
{
    using System.Collections.Generic;

    /// <summary>Names a bounded rerun from one exact retained failed verification.</summary>
    public sealed class HarborVerificationRerunRequest
    {
        /// <summary>Retained output identity from the failed full run.</summary>
        public string PriorOutputId { get; set; } = string.Empty;

        /// <summary>Check identity of the failed full run.</summary>
        public string PriorCheckId { get; set; } = string.Empty;

        /// <summary>Complete ordered test names selected by the shared rerun planner.</summary>
        public List<string> FailingTestNames { get; set; } = new List<string>();

        /// <summary>Optional bounded profile matcher used to validate names from the prior output.</summary>
        public string? FailedTestLinePattern { get; set; }

        /// <summary>Optional keyed host grant for the profile rerun command.</summary>
        public string? RerunCommandKey { get; set; }

        /// <summary>SHA-256 of the exact granted rerun command.</summary>
        public string? RerunCommandSha256 { get; set; }
    }
}
