namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;

    /// <summary>Selects a host-configured verification command without accepting an executable or shell text.</summary>
    public sealed class HarborVerificationRequest
    {
        /// <summary>Configured command key in the repository grant.</summary>
        public string CommandKey { get; set; } = String.Empty;

        /// <summary>SHA-256 of the server-approved configured command text.</summary>
        public string ExpectedCommandSha256 { get; set; } = String.Empty;

        /// <summary>Durable verification or gate attempt identity.</summary>
        public string CheckId { get; set; } = String.Empty;

        /// <summary>Relative directory within the pinned checkout.</summary>
        public string? RelativeWorkingDirectory { get; set; }

        /// <summary>Maximum time for the configured command.</summary>
        public int TimeoutSeconds { get; set; } = 1800;

        /// <summary>Per-stream output cap retained in the private runner spool.</summary>
        public int OutputLimitBytes { get; set; } = 4 * 1024 * 1024;

        /// <summary>Exit codes permitted by the existing verification policy.</summary>
        public List<int> AcceptedExitCodes { get; set; } = new List<int> { 0 };

        /// <summary>Optional structured isolated rerun derived from a retained failed verification.</summary>
        public HarborVerificationRerunRequest? Rerun { get; set; }

    }
}
