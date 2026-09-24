namespace Armada.Core
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;

    /// <summary>
    /// Cross-platform helpers for supervising launched agent processes. Uses only the .NET
    /// <see cref="Process"/> API, which behaves consistently on Windows, Linux, and macOS, so no
    /// OS-specific process handling is required. Process-tree termination is performed by the agent
    /// runtimes via <c>Process.Kill(entireProcessTree: true)</c>.
    /// </summary>
    public static class ProcessSupervisor
    {
        /// <summary>
        /// First synthetic process identifier. Synthetic identifiers start well above any real OS process
        /// identifier so a stray <see cref="Process.GetProcessById(int)"/> does not reach an unrelated live process.
        /// </summary>
        public const int SyntheticProcessIdFloor = 2_000_000_000;

        /// <summary>
        /// Tolerance applied when comparing a process start time against a launch reference, to
        /// absorb clock skew and launch latency.
        /// </summary>
        private static readonly TimeSpan _StartTimeTolerance = TimeSpan.FromMinutes(5);
        private static readonly ConcurrentDictionary<int, SyntheticProcess> _SyntheticProcesses = new ConcurrentDictionary<int, SyntheticProcess>();
        private static int _NextSyntheticProcessId = SyntheticProcessIdFloor;

        /// <summary>
        /// Allocate a synthetic process identifier. Every runtime that runs work outside a local OS process (an
        /// in-process loop, a Harbor runner job) takes its identifier here, so two runtimes never share one.
        /// </summary>
        /// <returns>A new synthetic process identifier.</returns>
        /// <exception cref="InvalidOperationException">The identifier range is exhausted.</exception>
        public static int AllocateSyntheticProcessId()
        {
            int processId = Interlocked.Increment(ref _NextSyntheticProcessId);
            if (processId <= SyntheticProcessIdFloor) throw new InvalidOperationException("synthetic_process_ids_exhausted");
            return processId;
        }

        /// <summary>
        /// Register a process identifier that is owned by an in-process runtime.
        /// </summary>
        /// <param name="processId">Synthetic process identifier.</param>
        public static void RegisterSyntheticProcess(int processId)
        {
            _SyntheticProcesses[processId] = new SyntheticProcess(null);
        }

        /// <summary>
        /// Register a synthetic process identifier together with the operation that stops its work. A runtime
        /// stop for the identifier runs that operation instead of looking for an OS process.
        /// </summary>
        /// <param name="processId">Synthetic process identifier.</param>
        /// <param name="stop">Stops the work behind the identifier.</param>
        public static void RegisterSyntheticProcess(int processId, Func<CancellationToken, Task> stop)
        {
            if (stop == null) throw new ArgumentNullException(nameof(stop));
            _SyntheticProcesses[processId] = new SyntheticProcess(stop);
        }

        /// <summary>
        /// Remove a process identifier that is owned by an in-process runtime.
        /// </summary>
        /// <param name="processId">Synthetic process identifier.</param>
        public static void UnregisterSyntheticProcess(int processId)
        {
            _SyntheticProcesses.TryRemove(processId, out _);
        }

        /// <summary>
        /// Whether a process identifier is currently owned by a running in-process runtime. This never
        /// consults the operating system, so an unrelated OS process with the same identifier cannot
        /// make an exited in-process run look alive.
        /// </summary>
        /// <param name="processId">Synthetic process identifier.</param>
        /// <returns>True while the in-process runtime keeps the identifier registered.</returns>
        public static bool IsSyntheticProcessAlive(int processId)
        {
            return _SyntheticProcesses.ContainsKey(processId);
        }

        /// <summary>
        /// Stop the work behind a live synthetic process identifier that was registered with a stop operation.
        /// </summary>
        /// <param name="processId">Process identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when the identifier is a registered synthetic process with a stop operation, which ran.</returns>
        public static async Task<bool> TryStopSyntheticProcessAsync(int processId, CancellationToken token = default)
        {
            if (!_SyntheticProcesses.TryGetValue(processId, out SyntheticProcess? process) || process.Stop == null) return false;
            await process.Stop(token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Determine whether the process with the given identifier is alive AND is not a recycled PID. The check goes
        /// through <see cref="OpenLaunchedProcess(int, DateTime?, out LaunchedProcessIdentityEnum)"/>: a live process
        /// whose start time differs from the launch recorded for the identifier, or is meaningfully later than
        /// <paramref name="launchedBeforeUtc"/>, is a different process and reads as not alive, so a stale captain is
        /// not kept Working because the OS reused its PID. A live process whose identity cannot be verified reads as
        /// alive.
        /// </summary>
        /// <param name="processId">OS process identifier.</param>
        /// <param name="launchedBeforeUtc">Approximate time the tracked process was launched (e.g. the mission start time), or null.</param>
        /// <returns>True if the process exists, has not exited, and is not a reused identifier.</returns>
        public static bool IsTrackedProcessAlive(int processId, DateTime? launchedBeforeUtc = null)
        {
            if (_SyntheticProcesses.ContainsKey(processId)) return true;
            LaunchedProcessIdentityEnum identity = ProbeLaunchedProcess(processId, launchedBeforeUtc);
            return identity == LaunchedProcessIdentityEnum.Verified || identity == LaunchedProcessIdentityEnum.Unverified;
        }

        /// <summary>
        /// Classify what holds a recorded agent process identifier now, without keeping a handle.
        /// </summary>
        /// <param name="processId">OS process identifier.</param>
        /// <param name="launchedBeforeUtc">Approximate launch time of the tracked process, or null.</param>
        /// <returns>Whether a live process holds the identifier and whether it is the launched one.</returns>
        public static LaunchedProcessIdentityEnum ProbeLaunchedProcess(int processId, DateTime? launchedBeforeUtc = null)
        {
            using (Process? process = OpenLaunchedProcess(processId, launchedBeforeUtc, out LaunchedProcessIdentityEnum identity))
            {
                return identity;
            }
        }

        /// <summary>
        /// Record the identity of a local process a runtime launched: its identifier and its start time. The
        /// record outlives the process, so a later process that receives the same identifier is recognized as a
        /// different one by its start time.
        /// </summary>
        /// <param name="processId">OS process identifier.</param>
        /// <param name="startedUtc">Start time of the launched process, in UTC.</param>
        public static void RecordLaunchedProcess(int processId, DateTime startedUtc)
        {
            if (processId <= 0) return;
            _LaunchedProcesses[processId] = startedUtc;
        }

        /// <summary>
        /// The recorded start time of the process a runtime launched with this identifier, for storing next to the
        /// identifier on a captain or mission record.
        /// </summary>
        /// <param name="processId">OS process identifier.</param>
        /// <returns>The recorded start time in UTC, or null when no launch was recorded for the identifier.</returns>
        public static DateTime? GetRecordedLaunchStartUtc(int processId)
        {
            if (processId <= 0) return null;
            return _LaunchedProcesses.TryGetValue(processId, out DateTime startedUtc) ? startedUtc : null;
        }

        /// <summary>
        /// Restore a launch identity read from a persisted record, so a process launched before the admiral process
        /// restarted is verified by its stored start time. A launch recorded in this admiral process is newer than any
        /// stored record and is never replaced.
        /// </summary>
        /// <param name="processId">OS process identifier.</param>
        /// <param name="startedUtc">Stored start time of the launched process, in UTC.</param>
        /// <returns>True when the identity was restored; false when this admiral process already holds one for the identifier.</returns>
        public static bool RestoreLaunchedProcess(int processId, DateTime startedUtc)
        {
            if (processId <= 0 || processId >= SyntheticProcessIdFloor) return false;
            return _LaunchedProcesses.TryAdd(processId, startedUtc);
        }

        /// <summary>
        /// Drop the in-memory launch identity for an identifier, which is the state an admiral process restart leaves.
        /// </summary>
        /// <param name="processId">OS process identifier.</param>
        public static void ForgetLaunchedProcess(int processId)
        {
            _LaunchedProcesses.TryRemove(processId, out _);
        }

        /// <summary>
        /// The one identity-checked lookup of a recorded agent process identifier; every stop, kill and liveness path
        /// for a local agent process goes through it. A live process is returned only when it is not provably a
        /// different process: when a launch was recorded for the identifier, its start time must match the recorded
        /// one (<see cref="LaunchedProcessIdentityEnum.Verified"/>); when none was recorded (for example a process
        /// launched before this admiral process started) or its start time cannot be read, it is returned as
        /// <see cref="LaunchedProcessIdentityEnum.Unverified"/>. A start time that differs from the recorded launch,
        /// or is later than <paramref name="launchedBeforeUtc"/> plus a tolerance, marks a reused identifier
        /// (<see cref="LaunchedProcessIdentityEnum.Reused"/>) and nothing is returned. Only a verified process may be
        /// killed.
        /// </summary>
        /// <param name="processId">OS process identifier.</param>
        /// <param name="launchedBeforeUtc">Approximate launch time of the tracked process, or null.</param>
        /// <param name="identity">What holds the identifier.</param>
        /// <returns>The live process, which the caller disposes, or null when none is running or it is a reused identifier.</returns>
        public static Process? OpenLaunchedProcess(int processId, DateTime? launchedBeforeUtc, out LaunchedProcessIdentityEnum identity)
        {
            identity = LaunchedProcessIdentityEnum.NotRunning;
            if (processId <= 0) return null;

            Process process;
            try
            {
                process = Process.GetProcessById(processId);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                // Unix reports an identifier that names no current process this way.
                return null;
            }

            bool keep = false;
            try
            {
                if (process.HasExited) return null;

                DateTime observedStartUtc;
                try
                {
                    observedStartUtc = process.StartTime.ToUniversalTime();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
                {
                    // Start time can be unavailable on some platforms or due to permissions: nothing proves which
                    // process this is.
                    identity = LaunchedProcessIdentityEnum.Unverified;
                    keep = true;
                    return process;
                }

                if (_LaunchedProcesses.TryGetValue(processId, out DateTime recordedStartUtc))
                {
                    if ((observedStartUtc - recordedStartUtc).Duration() > _LaunchIdentityTolerance)
                    {
                        identity = LaunchedProcessIdentityEnum.Reused;
                        return null;
                    }

                    identity = LaunchedProcessIdentityEnum.Verified;
                    keep = true;
                    return process;
                }

                if (launchedBeforeUtc.HasValue && observedStartUtc > launchedBeforeUtc.Value.Add(_StartTimeTolerance))
                {
                    // Started after the tracked process was launched: a recycled PID.
                    identity = LaunchedProcessIdentityEnum.Reused;
                    return null;
                }

                identity = LaunchedProcessIdentityEnum.Unverified;
                keep = true;
                return process;
            }
            catch (InvalidOperationException)
            {
                // The process exited while it was being inspected.
                identity = LaunchedProcessIdentityEnum.NotRunning;
                return null;
            }
            finally
            {
                if (!keep) process.Dispose();
            }
        }

        /// <summary>
        /// Largest difference between a recorded launch start time and a live process start time that still
        /// identifies the same process. Start times read through different calls can differ by clock rounding;
        /// a reused identifier belongs to a process that started after the recorded one exited.
        /// </summary>
        private static readonly TimeSpan _LaunchIdentityTolerance = TimeSpan.FromSeconds(2);
        private static readonly ConcurrentDictionary<int, DateTime> _LaunchedProcesses = new ConcurrentDictionary<int, DateTime>();

        private sealed class SyntheticProcess
        {
            public Func<CancellationToken, Task>? Stop { get; }

            public SyntheticProcess(Func<CancellationToken, Task>? stop)
            {
                Stop = stop;
            }
        }
    }
}
