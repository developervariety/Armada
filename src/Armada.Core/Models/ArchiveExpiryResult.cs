namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Outcome of one archive expiry run.
    /// </summary>
    public class ArchiveExpiryResult
    {
        /// <summary>
        /// True when the run listed the archive; false when the archive was not active.
        /// </summary>
        public bool Ran { get; set; } = false;

        /// <summary>
        /// Why the run did not list the archive, when <see cref="Ran"/> is false.
        /// </summary>
        public string? SkipReason { get; set; } = null;

        /// <summary>
        /// Objects listed under the raw and trace prefixes.
        /// </summary>
        public int Listed { get; set; } = 0;

        /// <summary>
        /// Objects deleted.
        /// </summary>
        public int Deleted { get; set; } = 0;

        /// <summary>
        /// Bytes of the objects deleted.
        /// </summary>
        public long DeletedBytes { get; set; } = 0;

        /// <summary>
        /// Deletes that failed.
        /// </summary>
        public int Failed { get; set; } = 0;

        /// <summary>
        /// Failed keys with their failure messages. Capped; <see cref="Failed"/> carries the full count.
        /// </summary>
        public List<string> Failures { get; set; } = new List<string>();
    }
}
