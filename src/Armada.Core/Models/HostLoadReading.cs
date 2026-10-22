namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One reading of the host's CPU load: the 1-minute and 15-minute load averages and the logical core
    /// count they are compared against.
    /// </summary>
    public class HostLoadReading
    {
        #region Public-Members

        /// <summary>
        /// 1-minute load average, or null when the host does not report one or it cannot be read.
        /// A null reading is treated as normal load.
        /// </summary>
        public double? LoadAverage1Min { get; set; } = null;

        /// <summary>
        /// 15-minute load average, or null when the host does not report one or it cannot be read. It
        /// describes the load across a long command, which the 1-minute average read when the command ends
        /// does not: the 1-minute average falls as soon as the command's own processes exit.
        /// </summary>
        public double? LoadAverage15Min { get; set; } = null;

        /// <summary>
        /// Logical core count of the host. Never below 1.
        /// </summary>
        public int LogicalCores
        {
            get => _LogicalCores;
            set => _LogicalCores = Math.Max(1, value);
        }

        /// <summary>
        /// UTC time of the reading.
        /// </summary>
        public DateTime ReadUtc { get; set; } = DateTime.UtcNow;

        #endregion

        #region Private-Members

        private int _LogicalCores = 1;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate with defaults.
        /// </summary>
        public HostLoadReading()
        {
        }

        #endregion
    }
}
