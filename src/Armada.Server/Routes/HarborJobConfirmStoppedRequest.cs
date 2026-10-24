namespace Armada.Server.Routes
{
    using System;

    /// <summary>Expected durable identity and named evidence for confirming a stopped Harbor orphan.</summary>
    public sealed class HarborJobConfirmStoppedRequest
    {
        /// <summary>Durable revision the operator inspected.</summary>
        public long ExpectedRevision { get; set; }

        /// <summary>Runner enrollment generation the operator inspected.</summary>
        public long EnrollmentGeneration { get; set; }

        /// <summary>Runner process identifier, or null when the runner never reported one.</summary>
        public int? RunnerProcessId { get; set; }

        /// <summary>Runner process start time, or null when the runner never reported one.</summary>
        public DateTime? RunnerProcessStartedUtc { get; set; }

        /// <summary>Named external evidence that verifies the process stopped.</summary>
        public string Evidence { get; set; } = String.Empty;
    }
}
