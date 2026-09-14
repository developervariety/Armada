namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Outcome of bounding the self-deploy release store.
    /// </summary>
    public sealed class SelfDeployReleasePruneResult
    {
        /// <summary>
        /// Digests of releases that were removed.
        /// </summary>
        public List<string> Removed { get; set; } = new List<string>();

        /// <summary>
        /// Digests of unprotected previous releases kept by the retention count.
        /// </summary>
        public List<string> RetainedPrevious { get; set; } = new List<string>();

        /// <summary>
        /// Stable reason when pruning was skipped or a release could not be removed.
        /// </summary>
        public string FailureReason { get; set; } = String.Empty;
    }
}
