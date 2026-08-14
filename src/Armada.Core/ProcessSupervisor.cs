namespace Armada.Core
{
    using System;
    using System.Diagnostics;

    /// <summary>
    /// Cross-platform helpers for supervising launched agent processes. Uses only the .NET
    /// <see cref="Process"/> API, which behaves consistently on Windows, Linux, and macOS, so no
    /// OS-specific process handling is required. Process-tree termination is performed by the agent
    /// runtimes via <c>Process.Kill(entireProcessTree: true)</c>.
    /// </summary>
    public static class ProcessSupervisor
    {
        /// <summary>
        /// Tolerance applied when comparing a process start time against a launch reference, to
        /// absorb clock skew and launch latency.
        /// </summary>
        private static readonly TimeSpan _StartTimeTolerance = TimeSpan.FromMinutes(5);

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
    }
}
