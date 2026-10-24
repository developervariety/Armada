namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Collections.Generic;

    /// <summary>Local policy for starting and retaining Harbor supervisor jobs.</summary>
    public sealed class HarborSupervisorOptions
    {
        /// <summary>Private directory that stores job metadata, output spools, and stop requests.</summary>
        public string StateDirectory { get; set; } = String.Empty;

        /// <summary>Roots that may contain a job working directory.</summary>
        public List<string> WorkingRoots { get; set; } = new List<string>();

        /// <summary>Configured runtime keys and their executable paths.</summary>
        public Dictionary<string, string> Commands { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Maximum bytes retained in one job output spool.</summary>
        public long MaxSpoolBytes { get; set; } = 16L * 1024L * 1024L;

        /// <summary>Maximum number of retained jobs.</summary>
        public int MaxRetainedJobs { get; set; } = 64;
    }
}
