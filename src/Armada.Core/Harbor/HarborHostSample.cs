namespace Armada.Core.Harbor
{
    /// <summary>
    /// One optional host reading on a Harbor heartbeat. A null property means the Harbor did not measure that
    /// value. It does not mean zero. The Admiral stores a sample only when at least one property has a value.
    /// </summary>
    public sealed class HarborHostSample
    {
        /// <summary>Host CPU use, from 0 through 100, when the Harbor measured it.</summary>
        public double? CpuPercent { get; set; }

        /// <summary>Host memory in use, in bytes, when measured.</summary>
        public long? MemoryUsedBytes { get; set; }

        /// <summary>Host memory size, in bytes, when measured.</summary>
        public long? MemoryTotalBytes { get; set; }

        /// <summary>Bytes used on the drive that holds the runner state, when measured.</summary>
        public long? DiskUsedBytes { get; set; }

        /// <summary>Size of the drive that holds the runner state, in bytes, when measured.</summary>
        public long? DiskTotalBytes { get; set; }

        /// <summary>Jobs the runner still reports as live, when the runner counted them.</summary>
        public int? ActiveJobs { get; set; }

        /// <summary>Live jobs whose child process is running, when the runner counted them.</summary>
        public int? ActiveCaptains { get; set; }
    }
}
