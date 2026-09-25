namespace Armada.Server
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Text.Json;

    /// <summary>
    /// Leaves evidence of how the previous admiral run ended. At start the run writes a marker file with
    /// its start time and process id; every health tick records the time and the process and container
    /// memory; a clean stop marks the run clean. A kill by the kernel OOM killer or by a container
    /// runtime runs no code, so the next start finds a marker with no clean mark and reports the last
    /// moment the run was known alive and how much memory it held then. Every file operation is
    /// best-effort: evidence must never stop the admiral.
    /// </summary>
    public sealed class AdmiralRunMarker
    {
        #region Public-Members

        /// <summary>File name of the marker inside the data directory.</summary>
        public const string FileName = "admiral-run.json";

        /// <summary>Event type recorded when the previous run ended with no stop requested.</summary>
        public const string UncleanExitEventType = "admiral.unclean_exit";

        /// <summary>Event type recorded when the previous run was asked to stop but did not finish stopping.</summary>
        public const string StopIncompleteEventType = "admiral.stop_incomplete";

        /// <summary>Full path of the marker file.</summary>
        public string Path { get; }

        #endregion

        #region Private-Members

        private readonly AdmiralRunRecord _Current;
        private readonly object _Lock = new object();

        #endregion

        #region Constructors-and-Factories

        private AdmiralRunMarker(string path, AdmiralRunRecord current)
        {
            Path = path;
            _Current = current;
        }

        /// <summary>
        /// Read the previous run's marker and start a new one.
        /// </summary>
        /// <param name="dataDirectory">Admiral data directory.</param>
        /// <param name="nowUtc">Current time.</param>
        /// <param name="processId">This process id.</param>
        /// <param name="previousUnclean">The previous run's record when it ended without a clean stop; otherwise null.</param>
        /// <returns>The marker for this run.</returns>
        public static AdmiralRunMarker Start(string dataDirectory, DateTime nowUtc, int processId, out AdmiralRunRecord? previousUnclean)
        {
            string path = System.IO.Path.Combine(dataDirectory ?? ".", FileName);
            previousUnclean = null;
            try
            {
                if (File.Exists(path))
                {
                    AdmiralRunRecord? previous = JsonSerializer.Deserialize<AdmiralRunRecord>(File.ReadAllText(path));
                    if (previous != null && previous.CleanExitUtc == null) previousUnclean = previous;
                }
            }
            catch (Exception)
            {
                // An unreadable marker is itself evidence of an interrupted write; there is nothing to report from it.
            }

            AdmiralRunMarker marker = new AdmiralRunMarker(path, new AdmiralRunRecord
            {
                StartUtc = nowUtc,
                ProcessId = processId,
                LastAliveUtc = nowUtc
            });
            marker.Write();
            return marker;
        }

        #endregion

        #region Public-Methods

        /// <summary>Record that the run is alive now, with its memory.</summary>
        /// <param name="nowUtc">Current time.</param>
        /// <param name="managedHeapBytes">Managed heap size.</param>
        /// <param name="containerMemoryBytes">Container memory in use, when the cgroup reports it.</param>
        /// <param name="containerMemoryLimitBytes">Container memory limit, when the cgroup reports one.</param>
        public void Beat(DateTime nowUtc, long managedHeapBytes, long? containerMemoryBytes, long? containerMemoryLimitBytes)
        {
            lock (_Lock)
            {
                if (_Current.CleanExitUtc != null) return;
                _Current.LastAliveUtc = nowUtc;
                _Current.LastManagedHeapBytes = managedHeapBytes;
                _Current.LastContainerMemoryBytes = containerMemoryBytes;
                _Current.LastContainerMemoryLimitBytes = containerMemoryLimitBytes;
                Write();
            }
        }

        /// <summary>Record that a stop was requested, before the shutdown sequence runs.</summary>
        /// <param name="nowUtc">Current time.</param>
        public void MarkStopRequested(DateTime nowUtc)
        {
            lock (_Lock)
            {
                _Current.StopRequestedUtc = nowUtc;
                Write();
            }
        }

        /// <summary>The event type that reports a previous run: a stop that did not finish, or a kill.</summary>
        /// <param name="previous">The previous run's record.</param>
        /// <returns>The event type.</returns>
        public static string EventTypeFor(AdmiralRunRecord previous)
        {
            if (previous == null) throw new ArgumentNullException(nameof(previous));
            return previous.StopRequestedUtc != null ? StopIncompleteEventType : UncleanExitEventType;
        }

        /// <summary>Mark this run as stopped cleanly.</summary>
        /// <param name="nowUtc">Current time.</param>
        public void MarkCleanExit(DateTime nowUtc)
        {
            lock (_Lock)
            {
                _Current.CleanExitUtc = nowUtc;
                Write();
            }
        }

        /// <summary>
        /// Describe a previous run that ended without a clean stop, for the log and the event.
        /// </summary>
        /// <param name="previous">The previous run's record.</param>
        /// <returns>The description.</returns>
        public static string Describe(AdmiralRunRecord previous)
        {
            if (previous == null) throw new ArgumentNullException(nameof(previous));
            if (previous.StopRequestedUtc != null)
            {
                return "The previous admiral run (pid " + previous.ProcessId.ToString(CultureInfo.InvariantCulture)
                    + ", started " + previous.StartUtc.ToString("o", CultureInfo.InvariantCulture)
                    + ") was asked to stop at " + previous.StopRequestedUtc.Value.ToString("o", CultureInfo.InvariantCulture)
                    + " but did not finish stopping; the container runtime's stop timeout most likely ended it.";
            }

            string text = "The previous admiral run (pid " + previous.ProcessId.ToString(CultureInfo.InvariantCulture)
                + ", started " + previous.StartUtc.ToString("o", CultureInfo.InvariantCulture)
                + ") ended with no stop requested. It was last known alive at "
                + previous.LastAliveUtc.ToString("o", CultureInfo.InvariantCulture);
            if (previous.LastManagedHeapBytes.HasValue)
                text += " with a managed heap of " + Megabytes(previous.LastManagedHeapBytes.Value);
            if (previous.LastContainerMemoryBytes.HasValue)
            {
                text += " and container memory of " + Megabytes(previous.LastContainerMemoryBytes.Value);
                if (previous.LastContainerMemoryLimitBytes.HasValue)
                    text += " of a " + Megabytes(previous.LastContainerMemoryLimitBytes.Value) + " limit";
            }
            text += ". A kill by the kernel OOM killer or the container runtime runs no shutdown code; "
                + "read the kernel journal around that time (journalctl -k) for the cause.";
            return text;
        }

        /// <summary>
        /// Read the container memory in use and its limit from cgroup v2, when available.
        /// </summary>
        /// <param name="currentBytes">Memory in use, or null.</param>
        /// <param name="limitBytes">Memory limit, or null when unlimited or unavailable.</param>
        public static void ReadContainerMemory(out long? currentBytes, out long? limitBytes)
        {
            currentBytes = ReadLong("/sys/fs/cgroup/memory.current");
            limitBytes = ReadLong("/sys/fs/cgroup/memory.max");
        }

        #endregion

        #region Private-Methods

        private void Write()
        {
            try
            {
                string temp = Path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_Current));
                File.Move(temp, Path, true);
            }
            catch (Exception)
            {
                // Evidence is best-effort; a full or read-only disk must not stop the admiral.
            }
        }

        private static long? ReadLong(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path).Trim();
                return Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Megabytes(long bytes)
        {
            return (bytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB";
        }

        #endregion
    }
}
