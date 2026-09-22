namespace Armada.Core
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;

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
        /// Determine whether the process with the given identifier is alive AND is plausibly the
        /// originally-launched process rather than a recycled PID. When <paramref name="launchedBeforeUtc"/>
        /// is supplied, a process whose start time is meaningfully later than the launch reference is
        /// treated as a different (recycled) process and reported as not alive, so stale captains are
        /// not left running because the OS reused their PID after a crash.
        /// </summary>
        /// <param name="processId">OS process identifier.</param>
        /// <param name="launchedBeforeUtc">Approximate time the tracked process was launched (e.g. the mission start time), or null to skip identity verification.</param>
        /// <returns>True if the process exists, has not exited, and matches the launch reference.</returns>
        public static bool IsTrackedProcessAlive(int processId, DateTime? launchedBeforeUtc = null)
        {
            if (_SyntheticProcesses.ContainsKey(processId)) return true;
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited) return false;

                if (launchedBeforeUtc.HasValue)
                {
                    DateTime startUtc;
                    try
                    {
                        startUtc = process.StartTime.ToUniversalTime();
                    }
                    catch
                    {
                        // Start time can be unavailable on some platforms or due to permissions;
                        // fall back to treating the live process as the tracked one.
                        return true;
                    }

                    if (startUtc > launchedBeforeUtc.Value.Add(_StartTimeTolerance))
                    {
                        // Started after the tracked process was launched -> a recycled PID.
                        return false;
                    }
                }

                return true;
            }
            catch (ArgumentException)
            {
                // No process with that identifier is running.
                return false;
            }
            catch
            {
                return false;
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
        /// Open a live process by identifier only when it is the process a runtime launched with that identifier.
        /// When a launch was recorded for the identifier, the live process must have the recorded start time; a
        /// different start time means the operating system reused the identifier, and nothing is returned. When no
        /// launch was recorded (for example a process launched before this admiral process started), the live
        /// process is returned with <paramref name="identityVerified"/> false.
        /// </summary>
        /// <param name="processId">OS process identifier.</param>
        /// <param name="identityVerified">True when the returned process matched a recorded launch.</param>
        /// <returns>The live process, which the caller disposes, or null when none matches.</returns>
        public static Process? OpenLaunchedProcess(int processId, out bool identityVerified)
        {
            identityVerified = false;
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
                if (!_LaunchedProcesses.TryGetValue(processId, out DateTime recordedStartUtc))
                {
                    keep = true;
                    return process;
                }

                DateTime observedStartUtc;
                try
                {
                    observedStartUtc = process.StartTime.ToUniversalTime();
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException)
                {
                    // A recorded launch whose live candidate cannot be identified is not acted on.
                    return null;
                }

                if ((observedStartUtc - recordedStartUtc).Duration() > _LaunchIdentityTolerance) return null;

                identityVerified = true;
                keep = true;
                return process;
            }
            catch (InvalidOperationException)
            {
                // The process exited while it was being inspected.
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
