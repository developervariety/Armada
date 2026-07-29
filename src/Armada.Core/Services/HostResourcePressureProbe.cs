namespace Armada.Core.Services
{
    using System;
    using System.Globalization;
    using System.IO;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Default resource-pressure probe. Reads the available (host/container)
    /// memory visible to the host and the current cgroup. The smallest reliable
    /// value wins so child agents, compilers, and sibling processes are included.
    /// Does not depend on swap and never kills processes.
    /// </summary>
    public sealed class HostResourcePressureProbe : IResourcePressureProbe
    {
        /// <summary>
        /// Take a resource-pressure snapshot.
        /// </summary>
        /// <returns>Snapshot of available host/container memory.</returns>
        public ResourcePressureSnapshot Probe()
        {
            long? availableBytes = ReadProcMemAvailableBytes();
            availableBytes = Minimum(availableBytes, ReadCgroupAvailableBytes());
            availableBytes ??= ReadGcAvailableBytes();

            return new ResourcePressureSnapshot
            {
                AvailableMemoryBytes = availableBytes,
                ObservedUtc = DateTime.UtcNow
            };
        }

        private static long? ReadProcMemAvailableBytes()
        {
            try
            {
                foreach (string line in File.ReadLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                        continue;

                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 &&
                        Int64.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long kibibytes))
                        return checked(kibibytes * 1024L);
                }
            }
            catch
            {
            }

            return null;
        }

        private static long? ReadCgroupAvailableBytes()
        {
            long? v2 = ReadCgroupPair("/sys/fs/cgroup/memory.max", "/sys/fs/cgroup/memory.current");
            if (v2.HasValue) return v2;

            return ReadCgroupPair(
                "/sys/fs/cgroup/memory/memory.limit_in_bytes",
                "/sys/fs/cgroup/memory/memory.usage_in_bytes");
        }

        private static long? ReadCgroupPair(string limitPath, string usagePath)
        {
            try
            {
                string limitText = File.ReadAllText(limitPath).Trim();
                if (String.Equals(limitText, "max", StringComparison.OrdinalIgnoreCase))
                    return null;

                string usageText = File.ReadAllText(usagePath).Trim();
                if (!Int64.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long limit) ||
                    !Int64.TryParse(usageText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long usage) ||
                    limit <= 0)
                    return null;

                return Math.Max(0, limit - usage);
            }
            catch
            {
                return null;
            }
        }

        private static long? ReadGcAvailableBytes()
        {
            try
            {
                GCMemoryInfo info = GC.GetGCMemoryInfo();
                return Math.Max(0, info.TotalAvailableMemoryBytes - info.HeapSizeBytes);
            }
            catch
            {
                return null;
            }
        }

        private static long? Minimum(long? left, long? right)
        {
            if (!left.HasValue) return right;
            if (!right.HasValue) return left;
            return Math.Min(left.Value, right.Value);
        }
    }
}
