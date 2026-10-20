namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// One measurement an <see cref="ArmadaMetricsRecorder"/> saw: the instrument, the value and its labels.
    /// </summary>
    public sealed class RecordedMeasurement
    {
        #region Public-Members

        /// <summary>
        /// Instrument name.
        /// </summary>
        public string Instrument { get; set; } = String.Empty;

        /// <summary>
        /// Recorded value.
        /// </summary>
        public double Value { get; set; } = 0;

        /// <summary>
        /// Labels, each value rendered as text.
        /// </summary>
        public Dictionary<string, string> Tags { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        #endregion

        #region Public-Methods

        /// <summary>
        /// True when the measurement carries the label with exactly this value.
        /// </summary>
        /// <param name="key">Label key.</param>
        /// <param name="value">Expected label value.</param>
        /// <returns>True on a match.</returns>
        public bool Has(string key, string value)
        {
            return Tags.TryGetValue(key, out string? actual) && String.Equals(actual, value, StringComparison.Ordinal);
        }

        #endregion
    }
}
