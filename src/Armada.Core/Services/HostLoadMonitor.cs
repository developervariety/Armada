namespace Armada.Core.Services
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Reads host CPU load and available memory, and decides whether the host is overloaded, so a command that timed out
    /// because the whole host ran slowly can be told apart from one that hangs at normal load.
    /// </summary>
    /// <remarks>
    /// Load is the 1-minute load average from <c>/proc/loadavg</c>, compared with the logical core count.
    /// The host is overloaded when the load average is above <see cref="OverloadFactor"/> times the core
    /// count. A long command ran under load when either the 1-minute or the 15-minute average is above that
    /// threshold (see <see cref="RanOverloaded"/>), because the 1-minute average falls as soon as the
    /// command's own processes exit. Load has fallen when the load average is at or below <see cref="RecoveredFactor"/> times the
    /// core count. On a host without <c>/proc/loadavg</c>, or when the file cannot be read or parsed, the
    /// reading is unknown: an unknown reading is never overloaded and always counts as fallen, so
    /// behavior there is the same as at normal load.
    /// </remarks>
    public class HostLoadMonitor
    {
        #region Public-Members

        /// <summary>
        /// The host is overloaded when its 1-minute load average is above this many times its logical
        /// core count.
        /// </summary>
        public const double OverloadFactor = 2.0;

        /// <summary>
        /// Load has fallen when the 1-minute load average is at or below this many times the logical core
        /// count.
        /// </summary>
        public const double RecoveredFactor = 1.0;

        /// <summary>
        /// How often <see cref="WaitForLoadToFallAsync"/> reads the load again while it waits.
        /// </summary>
        public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Longest <see cref="WaitForLoadToFallAsync"/> waits. When load has not fallen by then the wait
        /// ends anyway.
        /// </summary>
        public static readonly TimeSpan MaximumWait = TimeSpan.FromMinutes(20);

        #endregion

        #region Private-Members

        private readonly Func<double?> _LoadReader;
        private readonly Func<double?> _LongLoadReader;
        private readonly Func<double?> _MidLoadReader;
        private readonly Func<long?> _MemoryReader;
        private readonly Func<int> _CoreCountReader;
        private readonly Func<DateTime> _Clock;
        private readonly Func<TimeSpan, CancellationToken, Task> _Delay;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate. Every argument is optional; a null argument uses the production source.
        /// </summary>
        /// <param name="loadReader">Returns the 1-minute load average, or null when unknown. Defaults to
        /// reading <c>/proc/loadavg</c> on Linux and null elsewhere.</param>
        /// <param name="coreCountReader">Returns the logical core count. Defaults to
        /// <see cref="Environment.ProcessorCount"/>.</param>
        /// <param name="clock">Returns the current UTC time. Defaults to <see cref="DateTime.UtcNow"/>.</param>
        /// <param name="delay">Waits the given interval. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
        /// <param name="longLoadReader">Returns the 15-minute load average, or null when unknown. Defaults to
        /// reading <c>/proc/loadavg</c> on Linux and null elsewhere, except that a monitor given its own
        /// <paramref name="loadReader"/> and no long reader reads the 15-minute average as unknown, so a supplied
        /// 1-minute source is never mixed with the real host's 15-minute load.</param>
        /// <param name="midLoadReader">Returns the 5-minute load average, or null when unknown. Defaults the same way
        /// as <paramref name="longLoadReader"/>.</param>
        /// <param name="memoryReader">Returns the available memory in megabytes, or null when unknown. Defaults to the
        /// host command slot's reader (<see cref="HostWideCommandLock.MemoryReader"/>), except that a monitor given
        /// its own <paramref name="loadReader"/> and no memory reader reads memory as unknown.</param>
        public HostLoadMonitor(
            Func<double?>? loadReader = null,
            Func<int>? coreCountReader = null,
            Func<DateTime>? clock = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null,
            Func<double?>? longLoadReader = null,
            Func<double?>? midLoadReader = null,
            Func<long?>? memoryReader = null)
        {
            _LoadReader = loadReader ?? ReadLinuxLoadAverage;
            _LongLoadReader = longLoadReader ?? (loadReader == null ? ReadLinuxLongLoadAverage : UnknownLoadAverage);
            _MidLoadReader = midLoadReader ?? (loadReader == null ? ReadLinuxMidLoadAverage : UnknownLoadAverage);
            _MemoryReader = memoryReader ?? (loadReader == null ? ReadHostAvailableMemoryMb : UnknownMemory);
            _CoreCountReader = coreCountReader ?? DefaultCoreCount;
            _Clock = clock ?? DefaultClock;
            _Delay = delay ?? DefaultDelay;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Read the host load now. A reader that throws gives an unknown reading.
        /// </summary>
        /// <returns>The reading; never null.</returns>
        public HostLoadReading Read()
        {
            double? load;
            try
            {
                load = _LoadReader();
            }
            catch (Exception)
            {
                // An unreadable load is an unknown reading, which counts as normal load.
                load = null;
            }

            double? longLoad;
            try
            {
                longLoad = _LongLoadReader();
            }
            catch (Exception)
            {
                // An unreadable load is an unknown reading, which counts as normal load.
                longLoad = null;
            }

            double? midLoad;
            try
            {
                midLoad = _MidLoadReader();
            }
            catch (Exception)
            {
                // An unreadable load is an unknown reading, which counts as normal load.
                midLoad = null;
            }

            long? memoryMb;
            try
            {
                memoryMb = _MemoryReader();
            }
            catch (Exception)
            {
                // An unreadable memory figure is reported as unknown.
                memoryMb = null;
            }

            int cores;
            try
            {
                cores = _CoreCountReader();
            }
            catch (Exception)
            {
                cores = 1;
            }

            if (load.HasValue && (Double.IsNaN(load.Value) || Double.IsInfinity(load.Value) || load.Value < 0)) load = null;
            if (longLoad.HasValue && (Double.IsNaN(longLoad.Value) || Double.IsInfinity(longLoad.Value) || longLoad.Value < 0)) longLoad = null;
            if (midLoad.HasValue && (Double.IsNaN(midLoad.Value) || Double.IsInfinity(midLoad.Value) || midLoad.Value < 0)) midLoad = null;
            if (memoryMb.HasValue && memoryMb.Value < 0) memoryMb = null;

            return new HostLoadReading
            {
                LoadAverage1Min = load,
                LoadAverage5Min = midLoad,
                LoadAverage15Min = longLoad,
                LogicalCores = cores,
                AvailableMemoryMb = memoryMb,
                ReadUtc = _Clock()
            };
        }

        /// <summary>
        /// Whether a reading shows the host overloaded: a known load average above
        /// <see cref="OverloadFactor"/> times the core count.
        /// </summary>
        /// <param name="reading">The reading.</param>
        /// <returns>True when overloaded; false for normal load and for an unknown reading.</returns>
        public bool IsOverloaded(HostLoadReading reading)
        {
            if (reading == null) throw new ArgumentNullException(nameof(reading));
            if (!reading.LoadAverage1Min.HasValue) return false;
            return reading.LoadAverage1Min.Value > OverloadThreshold(reading.LogicalCores);
        }

        /// <summary>
        /// Whether a long command that ends at this reading ran under load: the 1-minute or the 15-minute
        /// load average is above <see cref="OverloadFactor"/> times the core count. The 1-minute average
        /// alone can read normal at the moment a long command ends, after load stood above the threshold
        /// for most of its run.
        /// </summary>
        /// <param name="reading">The reading taken when the command ended.</param>
        /// <returns>True when either known average is above the threshold; false otherwise.</returns>
        public bool RanOverloaded(HostLoadReading reading)
        {
            if (reading == null) throw new ArgumentNullException(nameof(reading));
            if (IsOverloaded(reading)) return true;
            if (!reading.LoadAverage15Min.HasValue) return false;
            return reading.LoadAverage15Min.Value > OverloadThreshold(reading.LogicalCores);
        }

        /// <summary>
        /// Wait while the host is overloaded, reading the load every <see cref="RecheckInterval"/>, for at most
        /// <see cref="MaximumWait"/>. Unlike <see cref="WaitForLoadToFallAsync"/>, the wait ends as soon as the
        /// host is no longer overloaded, not when load has fully fallen.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The last reading taken. <see cref="IsOverloaded"/> on it is true when the wait ended at the
        /// maximum.</returns>
        public async Task<HostLoadReading> WaitWhileOverloadedAsync(CancellationToken token = default)
        {
            HostLoadReading reading = Read();
            DateTime deadline = reading.ReadUtc + MaximumWait;
            while (IsOverloaded(reading) && reading.ReadUtc < deadline)
            {
                TimeSpan remaining = deadline - reading.ReadUtc;
                TimeSpan interval = remaining < RecheckInterval ? remaining : RecheckInterval;
                await _Delay(interval, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                reading = Read();
            }

            return reading;
        }

        /// <summary>
        /// Wait until the host is quiet enough to start a build: the 1-minute and the 5-minute load averages are both
        /// below the limit. A first reading that is already quiet admits at once, because the 5-minute average already
        /// covers the recent past. After a busy reading the wait needs <paramref name="quietReadings"/> quiet readings
        /// in a row, <see cref="RecheckInterval"/> apart, so a short dip inside a busy period does not admit. An
        /// unknown average counts as quiet. The wait ends at <see cref="MaximumWait"/> even when the host stays busy.
        /// </summary>
        /// <param name="limit">Load-average limit, or null for <see cref="RecoveredFactor"/> times the core count.</param>
        /// <param name="quietReadings">Quiet readings in a row needed after a busy reading; at least 1.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>How long the wait took, the limit, the last reading and whether it ended at the maximum.</returns>
        public async Task<HostQuietWait> WaitForQuietHostAsync(double? limit, int quietReadings, CancellationToken token = default)
        {
            if (quietReadings < 1) quietReadings = 1;
            HostLoadReading first = Read();
            double effectiveLimit = limit ?? RecoveredThreshold(first.LogicalCores);
            DateTime deadline = first.ReadUtc + MaximumWait;
            HostLoadReading reading = first;
            int quiet = IsQuiet(reading, effectiveLimit) ? quietReadings : 0;
            while (quiet < quietReadings && reading.ReadUtc < deadline)
            {
                TimeSpan remaining = deadline - reading.ReadUtc;
                await _Delay(remaining < RecheckInterval ? remaining : RecheckInterval, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                reading = Read();
                quiet = IsQuiet(reading, effectiveLimit) ? quiet + 1 : 0;
            }

            long waited = Convert.ToInt64(Math.Max(0, (reading.ReadUtc - first.ReadUtc).TotalMilliseconds));
            return new HostQuietWait
            {
                Limit = effectiveLimit,
                WaitedMs = waited,
                EndedAtMaximum = quiet < quietReadings,
                Reading = reading
            };
        }

        /// <summary>
        /// Whether a reading shows load has fallen: a load average at or below
        /// <see cref="RecoveredFactor"/> times the core count, or an unknown reading.
        /// </summary>
        /// <param name="reading">The reading.</param>
        /// <returns>True when load has fallen or is unknown.</returns>
        public bool HasFallen(HostLoadReading reading)
        {
            if (reading == null) throw new ArgumentNullException(nameof(reading));
            if (!reading.LoadAverage1Min.HasValue) return true;
            return reading.LoadAverage1Min.Value <= RecoveredThreshold(reading.LogicalCores);
        }

        /// <summary>
        /// Wait until load has fallen, reading it every <see cref="RecheckInterval"/>, for at most
        /// <see cref="MaximumWait"/>.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The last reading taken. <see cref="HasFallen"/> on it is false when the wait ended at
        /// the maximum.</returns>
        public async Task<HostLoadReading> WaitForLoadToFallAsync(CancellationToken token = default)
        {
            HostLoadReading reading = Read();
            DateTime deadline = reading.ReadUtc + MaximumWait;
            while (!HasFallen(reading) && reading.ReadUtc < deadline)
            {
                TimeSpan remaining = deadline - reading.ReadUtc;
                TimeSpan interval = remaining < RecheckInterval ? remaining : RecheckInterval;
                await _Delay(interval, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                reading = Read();
            }

            return reading;
        }

        /// <summary>
        /// Load average above which a host with the given core count is overloaded.
        /// </summary>
        /// <param name="logicalCores">Logical core count.</param>
        /// <returns>The threshold.</returns>
        public static double OverloadThreshold(int logicalCores)
        {
            return OverloadFactor * Math.Max(1, logicalCores);
        }

        /// <summary>
        /// Load average at or below which load on a host with the given core count has fallen.
        /// </summary>
        /// <param name="logicalCores">Logical core count.</param>
        /// <returns>The threshold.</returns>
        public static double RecoveredThreshold(int logicalCores)
        {
            return RecoveredFactor * Math.Max(1, logicalCores);
        }

        /// <summary>
        /// Read the 1-minute load average from the text of <c>/proc/loadavg</c>.
        /// </summary>
        /// <param name="content">The file's text, for example <c>3.52 2.10 1.07 2/1234 5678</c>.</param>
        /// <returns>The 1-minute load average, or null when the text is empty or cannot be parsed.</returns>
        public static double? ParseLoadAverage(string? content)
        {
            return ParseLoadAverageField(content, 0);
        }

        /// <summary>
        /// Read the 5-minute load average from the text of <c>/proc/loadavg</c>.
        /// </summary>
        /// <param name="content">The file's text, for example <c>3.52 2.10 1.07 2/1234 5678</c>.</param>
        /// <returns>The 5-minute load average, or null when the text is empty, too short, or cannot be parsed.</returns>
        public static double? ParseMidLoadAverage(string? content)
        {
            return ParseLoadAverageField(content, 1);
        }

        /// <summary>
        /// Read the 15-minute load average from the text of <c>/proc/loadavg</c>.
        /// </summary>
        /// <param name="content">The file's text, for example <c>3.52 2.10 1.07 2/1234 5678</c>.</param>
        /// <returns>The 15-minute load average, or null when the text is empty, too short, or cannot be parsed.</returns>
        public static double? ParseLongLoadAverage(string? content)
        {
            return ParseLoadAverageField(content, 2);
        }

        #endregion

        #region Private-Methods

        private static bool IsQuiet(HostLoadReading reading, double limit)
        {
            return (!reading.LoadAverage1Min.HasValue || reading.LoadAverage1Min.Value < limit)
                && (!reading.LoadAverage5Min.HasValue || reading.LoadAverage5Min.Value < limit);
        }

        /// <summary>
        /// Read the 1-minute load average from <c>/proc/loadavg</c> on Linux. Returns null on any other
        /// platform and on any read or parse failure.
        /// </summary>
        private static double? ReadLinuxLoadAverage()
        {
            if (!OperatingSystem.IsLinux()) return null;
            try
            {
                return ParseLoadAverage(File.ReadAllText("/proc/loadavg"));
            }
            catch (Exception)
            {
                // An unreadable file is an unknown reading, which counts as normal load.
                return null;
            }
        }

        /// <summary>
        /// Read the 15-minute load average from <c>/proc/loadavg</c> on Linux. Returns null on any other
        /// platform and on any read or parse failure.
        /// </summary>
        private static double? ReadLinuxLongLoadAverage()
        {
            if (!OperatingSystem.IsLinux()) return null;
            try
            {
                return ParseLongLoadAverage(File.ReadAllText("/proc/loadavg"));
            }
            catch (Exception)
            {
                // An unreadable file is an unknown reading, which counts as normal load.
                return null;
            }
        }

        /// <summary>
        /// Read the 5-minute load average from <c>/proc/loadavg</c> on Linux. Returns null on any other
        /// platform and on any read or parse failure.
        /// </summary>
        private static double? ReadLinuxMidLoadAverage()
        {
            if (!OperatingSystem.IsLinux()) return null;
            try
            {
                return ParseMidLoadAverage(File.ReadAllText("/proc/loadavg"));
            }
            catch (Exception)
            {
                // An unreadable file is an unknown reading, which counts as normal load.
                return null;
            }
        }

        private static long? ReadHostAvailableMemoryMb()
        {
            return HostWideCommandLock.MemoryReader();
        }

        private static double? UnknownLoadAverage()
        {
            return null;
        }

        private static long? UnknownMemory()
        {
            return null;
        }

        private static double? ParseLoadAverageField(string? content, int field)
        {
            if (String.IsNullOrWhiteSpace(content)) return null;
            string[] parts = content.Trim().Split(new char[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= field) return null;
            if (!Double.TryParse(parts[field], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value)) return null;
            return value;
        }

        private static int DefaultCoreCount()
        {
            return Environment.ProcessorCount;
        }

        private static DateTime DefaultClock()
        {
            return DateTime.UtcNow;
        }

        private static Task DefaultDelay(TimeSpan interval, CancellationToken token)
        {
            return Task.Delay(interval, token);
        }

        #endregion
    }
}
