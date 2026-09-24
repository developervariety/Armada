namespace Test.Shared.Infrastructure
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A shell fixture whose background child leaves the process tree: a subshell starts a long sleep, records its
    /// process identifier and exits, so the sleep is re-parented and only a process-group kill reaches it. Used to
    /// prove that a runner owns a process group from launch.
    /// </summary>
    public static class BackgroundChildProbe
    {
        /// <summary>
        /// A new path for the background child's process identifier.
        /// </summary>
        /// <returns>A path under the temporary directory that does not exist yet.</returns>
        public static string NewPidFile()
        {
            return Path.Combine(Path.GetTempPath(), "armada-background-child-" + Guid.NewGuid().ToString("N") + ".pid");
        }

        /// <summary>
        /// The <c>/bin/sh -c</c> script: start the orphaned sleep, record it, then keep the shell running.
        /// </summary>
        /// <param name="pidFile">File that receives the background child's process identifier.</param>
        /// <returns>The script text.</returns>
        public static string Script(string pidFile)
        {
            return "(sleep 60 >/dev/null 2>&1 & echo $! > '" + pidFile + "'); sleep 60";
        }

        /// <summary>
        /// Read the background child's process identifier, waiting briefly for the shell to write it.
        /// </summary>
        /// <param name="pidFile">File the script writes.</param>
        /// <returns>The process identifier.</returns>
        public static int ReadPid(string pidFile)
        {
            for (int i = 0; i < 100 && !File.Exists(pidFile); i++) Thread.Sleep(20);
            return Int32.Parse(File.ReadAllText(pidFile).Trim());
        }

        /// <summary>
        /// Wait up to five seconds for the process to be gone.
        /// </summary>
        /// <param name="pid">Process identifier.</param>
        /// <returns>True when the process no longer runs.</returns>
        public static async Task<bool> GoneAsync(int pid)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (!IsAlive(pid)) return true;
                await Task.Delay(50).ConfigureAwait(false);
            }

            return !IsAlive(pid);
        }

        /// <summary>
        /// Kill the recorded background child if it still runs, and delete the file.
        /// </summary>
        /// <param name="pidFile">File the script writes.</param>
        public static void Cleanup(string pidFile)
        {
            if (!File.Exists(pidFile)) return;
            if (Int32.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
            {
                try
                {
                    using (Process process = Process.GetProcessById(pid))
                    {
                        process.Kill();
                    }
                }
                catch (ArgumentException)
                {
                    // Already gone.
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
            }

            File.Delete(pidFile);
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
