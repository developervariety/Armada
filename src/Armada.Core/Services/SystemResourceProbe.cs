namespace Armada.Core.Services
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Runtime.InteropServices;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Reads host memory from the operating system: <c>GlobalMemoryStatusEx</c> on Windows and
    /// <c>/proc/meminfo</c> on Linux. When the figure cannot be read (unsupported OS or a failed call) it
    /// returns a non-positive value, which the admission logic treats as "unmeasurable" and fails open, so
    /// a probe error never wedges dispatch.
    /// </summary>
    public sealed class SystemResourceProbe : ISystemResourceProbe
    {
        #region Public-Methods

        /// <inheritdoc />
        public long GetAvailablePhysicalBytes()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    MEMORYSTATUSEX status = new MEMORYSTATUSEX();
                    status.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
                    if (GlobalMemoryStatusEx(ref status)) return (long)status.ullAvailPhys;
                    return 0;
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return ReadMemInfoKilobytes("MemAvailable") * 1024L;
                }
            }
            catch
            {
            }

            return 0;
        }

        /// <inheritdoc />
        public long GetTotalPhysicalBytes()
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    MEMORYSTATUSEX status = new MEMORYSTATUSEX();
                    status.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
                    if (GlobalMemoryStatusEx(ref status)) return (long)status.ullTotalPhys;
                    return 0;
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    return ReadMemInfoKilobytes("MemTotal") * 1024L;
                }
            }
            catch
            {
            }

            return 0;
        }

        #endregion

        #region Private-Methods

        private static long ReadMemInfoKilobytes(string key)
        {
            if (!File.Exists("/proc/meminfo")) return 0;

            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith(key + ":", StringComparison.Ordinal)) continue;

                string rest = line.Substring(key.Length + 1).Trim();
                int space = rest.IndexOf(' ');
                if (space > 0) rest = rest.Substring(0, space);
                if (long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out long kb)) return kb;
                return 0;
            }

            return 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        #endregion
    }
}
