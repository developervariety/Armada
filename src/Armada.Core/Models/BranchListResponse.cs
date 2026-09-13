namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Read-only branch listing for a vessel repository.
    /// </summary>
    public class BranchListResponse
    {
        /// <summary>Vessel identifier.</summary>
        public string VesselId { get; set; } = String.Empty;

        /// <summary>Configured default branch.</summary>
        public string DefaultBranch { get; set; } = "main";

        /// <summary>Repository source used for inspection, such as LocalPath or WorkingDirectory.</summary>
        public string Source { get; set; } = String.Empty;

        /// <summary>Repository HEAD state: attached, detached, bare, or unknown.</summary>
        public string HeadState { get; set; } = "unknown";

        /// <summary>Symbolic HEAD ref when the repository has one.</summary>
        public string? HeadRef { get; set; }

        /// <summary>Local repository branches.</summary>
        public List<BranchInfo> Branches { get; set; } = new List<BranchInfo>();

        /// <summary>Total branch count.</summary>
        public int BranchCount { get; set; }

        /// <summary>Read error, when the listing could not be produced.</summary>
        public string? Error { get; set; }
    }
}
