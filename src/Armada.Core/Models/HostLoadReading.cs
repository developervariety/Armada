namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// One reading of the host's CPU load: the 1-minute load average and the logical core count it is
    /// compared against.
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
