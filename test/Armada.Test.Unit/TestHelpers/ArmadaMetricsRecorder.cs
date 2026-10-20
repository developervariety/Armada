namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Metrics;
    using System.Linq;
    using Armada.Core;

    /// <summary>
    /// Listens to the Armada meter the way a telemetry host does and keeps every measurement taken while it is
    /// open. The meter is process-wide, so a reader asserts that the measurements it expects are present and
    /// never that nothing else was recorded.
    /// </summary>
    public sealed class ArmadaMetricsRecorder : IDisposable
    {
        #region Private-Members

        private readonly MeterListener _Listener;
        private readonly object _Sync = new object();
        private readonly List<RecordedMeasurement> _Measurements = new List<RecordedMeasurement>();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Start listening to every Armada instrument.
        /// </summary>
        public ArmadaMetricsRecorder()
        {
            _Listener = new MeterListener();
            _Listener.InstrumentPublished = (instrument, listener) =>
            {
                if (String.Equals(instrument.Meter.Name, ArmadaMetrics.MeterName, StringComparison.Ordinal))
                    listener.EnableMeasurementEvents(instrument);
            };
            _Listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) => Add(instrument, value, tags));
            _Listener.SetMeasurementEventCallback<double>((instrument, value, tags, state) => Add(instrument, value, tags));
            _Listener.Start();
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Every measurement of one instrument, in the order taken.
        /// </summary>
        /// <param name="instrument">Instrument name.</param>
        /// <returns>A copy of the matching measurements.</returns>
        public List<RecordedMeasurement> Of(string instrument)
        {
            lock (_Sync)
            {
                return _Measurements.Where(m => String.Equals(m.Instrument, instrument, StringComparison.Ordinal)).ToList();
            }
        }

        /// <summary>
        /// Every measurement taken so far.
        /// </summary>
        /// <returns>A copy of the measurements.</returns>
        public List<RecordedMeasurement> All()
        {
            lock (_Sync)
            {
                return new List<RecordedMeasurement>(_Measurements);
            }
        }

        /// <summary>
        /// Stop listening.
        /// </summary>
        public void Dispose()
        {
            _Listener.Dispose();
        }

        #endregion

        #region Private-Methods

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            RecordedMeasurement measurement = new RecordedMeasurement();
            measurement.Instrument = instrument.Name;
            measurement.Value = value;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                measurement.Tags[tag.Key] = tag.Value?.ToString() ?? String.Empty;
            }

            lock (_Sync)
            {
                _Measurements.Add(measurement);
            }
        }

        #endregion
    }
}
