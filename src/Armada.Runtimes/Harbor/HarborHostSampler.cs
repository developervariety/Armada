namespace Armada.Runtimes.Harbor
{
    using System;
    using System.IO;
    using Armada.Core.Harbor;

    /// <summary>
    /// Fills the optional heartbeat host sample from this machine. A counter that cannot be read stays null.
    /// The first CPU observation has no delta, so its CPU percent is null.
    /// </summary>
    internal sealed class HarborHostSampler
    {
        private string? _PreviousCpuLine;

        /// <summary>Measure the host and the live job counts.</summary>
        /// <param name="activeJobs">Live jobs on this runner.</param>
        /// <param name="activeCaptains">Live jobs whose child process is running.</param>
        /// <param name="diskPath">Path whose drive is measured. A missing drive leaves disk fields null.</param>
        /// <returns>A sample. Unread fields are null.</returns>
        public HarborHostSample Capture(int activeJobs, int activeCaptains, string diskPath)
        {
            HarborHostSample sample = new HarborHostSample
            {
                ActiveJobs = activeJobs >= 0 ? activeJobs : null,
                ActiveCaptains = activeCaptains >= 0 ? activeCaptains : null
            };
            try
            {
                string? current = ReadCpuLine();
                sample.CpuPercent = HarborHostProbe.CpuPercent(_PreviousCpuLine, current);
                if (current != null) _PreviousCpuLine = current;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
            }

            try
            {
                if (File.Exists("/proc/meminfo"))
                    HarborHostProbe.ReadMemory(sample, File.ReadAllText("/proc/meminfo"));
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
            }

            try
            {
                if (!String.IsNullOrWhiteSpace(diskPath))
                {
                    string full = Path.GetFullPath(diskPath);
                    string root = Path.GetPathRoot(full) ?? full;
                    DriveInfo drive = new DriveInfo(root);
                    if (drive.IsReady)
                        HarborHostProbe.ReadDisk(sample, drive.TotalSize, drive.AvailableFreeSpace);
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException)
            {
            }

            return sample;
        }

        private static string? ReadCpuLine()
        {
            if (!File.Exists("/proc/stat")) return null;
            foreach (string line in File.ReadLines("/proc/stat"))
            {
                if (line.StartsWith("cpu ", StringComparison.Ordinal)) return line;
            }
            return null;
        }
    }
}
