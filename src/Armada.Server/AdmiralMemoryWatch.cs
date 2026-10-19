namespace Armada.Server
{
    using System;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;

    /// <summary>
    /// Samples the admiral's own memory every few seconds and, while the managed heap or the working set is
    /// at or above a threshold, logs a warning that names the memory figures and the REST requests still
    /// running. A request that drives the process to the out-of-memory killer is usually the one still in
    /// flight, and the request log only records requests that complete, so this line is what names it.
    /// While above the threshold every sample also calls the pressure callback (the run-marker beat), so
    /// the unclean-exit record carries memory figures from seconds before a kill. The warning repeats when
    /// memory grows by another <see cref="GrowthStepBytes"/> or after <see cref="RepeatInterval"/>.
    /// </summary>
    public sealed class AdmiralMemoryWatch
    {
        #region Public-Members

        /// <summary>Default threshold: 6 GiB.</summary>
        public const long DefaultThresholdBytes = 6L * 1024 * 1024 * 1024;

        /// <summary>Default growth that repeats the warning before <see cref="RepeatInterval"/>: 1 GiB.</summary>
        public const long DefaultGrowthStepBytes = 1L * 1024 * 1024 * 1024;

        /// <summary>Most running requests named in one warning.</summary>
        public const int MaxRequestsNamed = 8;

        /// <summary>Default time between samples.</summary>
        public static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(5);

        /// <summary>Default time after which the warning repeats while memory stays above the threshold.</summary>
        public static readonly TimeSpan DefaultRepeatInterval = TimeSpan.FromSeconds(60);

        /// <summary>Managed heap or working set, in bytes, at or above which a sample is reported.</summary>
        public long ThresholdBytes { get; }

        /// <summary>Growth since the last warning that repeats it early.</summary>
        public long GrowthStepBytes { get; }

        /// <summary>Time between samples.</summary>
        public TimeSpan SampleInterval { get; }

        /// <summary>Time after which the warning repeats while memory stays above the threshold.</summary>
        public TimeSpan RepeatInterval { get; }

        #endregion

        #region Private-Members

        private readonly string _Header = "[AdmiralMemoryWatch] ";
        private readonly LoggingModule _Logging;
        private readonly InFlightRequestRegistry _Requests;
        private readonly Action? _OnPressure;
        private DateTime _LastReportUtc = DateTime.MinValue;
        private long _LastReportedBytes = 0;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="logging">Logging module the warning is written to.</param>
        /// <param name="requests">Running REST requests named in the warning.</param>
        /// <param name="onPressure">Called on every sample at or above the threshold; may be null.</param>
        /// <param name="thresholdBytes">Threshold in bytes; at least 1 MiB.</param>
        /// <param name="growthStepBytes">Growth that repeats the warning early; at least 1 MiB.</param>
        /// <param name="sampleInterval">Time between samples; at least 1 second. Null uses <see cref="DefaultSampleInterval"/>.</param>
        /// <param name="repeatInterval">Time after which the warning repeats. Null uses <see cref="DefaultRepeatInterval"/>.</param>
        public AdmiralMemoryWatch(
            LoggingModule logging,
            InFlightRequestRegistry requests,
            Action? onPressure = null,
            long thresholdBytes = DefaultThresholdBytes,
            long growthStepBytes = DefaultGrowthStepBytes,
            TimeSpan? sampleInterval = null,
            TimeSpan? repeatInterval = null)
        {
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
            _Requests = requests ?? throw new ArgumentNullException(nameof(requests));
            _OnPressure = onPressure;
            ThresholdBytes = Math.Max(1024L * 1024, thresholdBytes);
            GrowthStepBytes = Math.Max(1024L * 1024, growthStepBytes);
            TimeSpan interval = sampleInterval ?? DefaultSampleInterval;
            SampleInterval = interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : interval;
            TimeSpan repeat = repeatInterval ?? DefaultRepeatInterval;
            RepeatInterval = repeat < TimeSpan.Zero ? TimeSpan.Zero : repeat;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Evaluate one sample: call the pressure callback and, when due, log the warning.
        /// </summary>
        /// <param name="nowUtc">Sample time.</param>
        /// <param name="managedHeapBytes">Managed heap size.</param>
        /// <param name="workingSetBytes">Process working set.</param>
        /// <param name="containerBytes">Container memory in use, when known.</param>
        /// <param name="containerLimitBytes">Container memory limit, when known.</param>
        /// <returns>The warning text when one was logged; otherwise null.</returns>
        public string? Sample(DateTime nowUtc, long managedHeapBytes, long workingSetBytes, long? containerBytes, long? containerLimitBytes)
        {
            long largest = Math.Max(managedHeapBytes, workingSetBytes);
            if (largest < ThresholdBytes)
            {
                _LastReportedBytes = 0;
                return null;
            }

            try
            {
                _OnPressure?.Invoke();
            }
            catch (Exception ex)
            {
                _Logging.Debug(_Header + "pressure callback failed: " + ex.Message);
            }

            bool due = _LastReportedBytes == 0
                || largest >= _LastReportedBytes + GrowthStepBytes
                || nowUtc - _LastReportUtc >= RepeatInterval;
            if (!due) return null;

            _LastReportedBytes = largest;
            _LastReportUtc = nowUtc;
            string text = "admiral memory above " + Megabytes(ThresholdBytes)
                + ": managed heap " + Megabytes(managedHeapBytes)
                + ", working set " + Megabytes(workingSetBytes)
                + (containerBytes.HasValue
                    ? ", container " + Megabytes(containerBytes.Value)
                        + (containerLimitBytes.HasValue ? " of " + Megabytes(containerLimitBytes.Value) : "")
                    : "")
                + "; requests in flight: " + _Requests.Describe(nowUtc, MaxRequestsNamed);
            _Logging.Warn(_Header + text);
            return text;
        }

        /// <summary>
        /// Sample until cancelled. A failed sample is logged at debug level and the loop continues.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public async Task RunAsync(CancellationToken token = default)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(SampleInterval, token).ConfigureAwait(false);
                    long? containerBytes;
                    long? containerLimitBytes;
                    AdmiralRunMarker.ReadContainerMemory(out containerBytes, out containerLimitBytes);
                    Sample(DateTime.UtcNow, GC.GetTotalMemory(false), Environment.WorkingSet, containerBytes, containerLimitBytes);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _Logging.Debug(_Header + "sample failed: " + ex.Message);
                }
            }
        }

        #endregion

        #region Private-Methods

        private static string Megabytes(long bytes)
        {
            return (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB";
        }

        #endregion
    }
}
