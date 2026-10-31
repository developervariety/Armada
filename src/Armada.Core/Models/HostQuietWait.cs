namespace Armada.Core.Models
{
    using System;
    using System.Globalization;

    /// <summary>
    /// The result of waiting for a quiet host before a build: how long the wait took, the limit it used, the last
    /// reading, and whether it ended at the maximum wait with the host still busy.
    /// </summary>
    public class HostQuietWait
    {
        #region Public-Members

        /// <summary>
        /// The load-average limit the 1-minute and 5-minute averages had to be below.
        /// </summary>
        public double Limit { get; set; } = 0;

        /// <summary>
        /// Milliseconds from the first reading to admission.
        /// </summary>
        public long WaitedMs { get; set; } = 0;

        /// <summary>
        /// True when the wait reached its maximum while the host was still busy, and the build started anyway.
        /// </summary>
        public bool EndedAtMaximum { get; set; } = false;

        /// <summary>
        /// The reading the wait ended on.
        /// </summary>
        public HostLoadReading Reading { get; set; } = new HostLoadReading();

        #endregion

        #region Public-Methods

        /// <summary>
        /// One line naming the wait, the limit and the last 1-minute and 5-minute averages.
        /// </summary>
        /// <returns>The line, without a trailing newline.</returns>
        public string Describe()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            string load1 = Reading.LoadAverage1Min.HasValue ? Reading.LoadAverage1Min.Value.ToString("0.##", inv) : "unknown";
            string load5 = Reading.LoadAverage5Min.HasValue ? Reading.LoadAverage5Min.Value.ToString("0.##", inv) : "unknown";
            return "load wait: " + WaitedMs.ToString(inv) + " ms, 1m/5m " + load1 + "/" + load5
                + ", limit " + Limit.ToString("0.##", inv)
                + (EndedAtMaximum ? "; load wait ended at maximum, the build runs on a busy host" : String.Empty);
        }

        #endregion
    }
}
