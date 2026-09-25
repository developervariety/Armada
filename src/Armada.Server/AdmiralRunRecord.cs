namespace Armada.Server
{
    using System;

    /// <summary>
    /// One admiral run as recorded in the run marker file. See <see cref="AdmiralRunMarker"/>.
    /// </summary>
    public sealed class AdmiralRunRecord
    {
        #region Public-Members

        /// <summary>When the run started.</summary>
        public DateTime StartUtc { get; set; } = DateTime.UtcNow;

        /// <summary>The run's process id.</summary>
        public int ProcessId { get; set; } = 0;

        /// <summary>The last time the run recorded that it was alive.</summary>
        public DateTime LastAliveUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Managed heap size at the last beat.</summary>
        public long? LastManagedHeapBytes { get; set; } = null;

        /// <summary>Container memory in use at the last beat, when the cgroup reports it.</summary>
        public long? LastContainerMemoryBytes { get; set; } = null;

        /// <summary>Container memory limit at the last beat, when the cgroup reports one.</summary>
        public long? LastContainerMemoryLimitBytes { get; set; } = null;

        /// <summary>When the run stopped cleanly; null while it runs or when it was killed.</summary>
        public DateTime? CleanExitUtc { get; set; } = null;

        #endregion
    }
}
