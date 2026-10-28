namespace Armada.Core.Harbor
{
    using System;
    using System.Globalization;

    /// <summary>
    /// Reads host counters into a <see cref="HarborHostSample"/>. A counter the Harbor cannot read stays null.
    /// A measured zero stays zero.
    /// </summary>
    public static class HarborHostProbe
    {
        /// <summary>
        /// CPU percent between two <c>/proc/stat</c> <c>cpu</c> lines. The first pair has no delta, and an unreadable
        /// or decreasing counter has no percent. Both of those results are null.
        /// </summary>
        /// <param name="previousCpuLine">Earlier aggregate <c>cpu</c> line, or null.</param>
        /// <param name="currentCpuLine">Later aggregate <c>cpu</c> line, or null.</param>
        /// <returns>Percent from 0 through 100, or null when the pair cannot be measured.</returns>
        public static double? CpuPercent(string? previousCpuLine, string? currentCpuLine)
        {
            if (!TryReadCpuCounters(previousCpuLine, out long previousIdle, out long previousTotal)) return null;
            if (!TryReadCpuCounters(currentCpuLine, out long currentIdle, out long currentTotal)) return null;
            long idleDelta = currentIdle - previousIdle;
            long totalDelta = currentTotal - previousTotal;
            if (totalDelta <= 0 || idleDelta < 0 || idleDelta > totalDelta) return null;
            double percent = (1.0d - ((double)idleDelta / totalDelta)) * 100.0d;
            if (Double.IsNaN(percent) || Double.IsInfinity(percent) || percent < 0d || percent > 100d) return null;
            return Math.Round(percent, 1, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Fill memory fields from <c>/proc/meminfo</c> text. Both total and available are required. A missing line
        /// leaves both fields null.
        /// </summary>
        /// <param name="sample">Sample to fill.</param>
        /// <param name="memInfo">Meminfo text, or null.</param>
        public static void ReadMemory(HarborHostSample sample, string? memInfo)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            if (String.IsNullOrWhiteSpace(memInfo)) return;
            long? totalKibibytes = null;
            long? availableKibibytes = null;
            foreach (string raw in memInfo.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) totalKibibytes = Kibibytes(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) availableKibibytes = Kibibytes(line);
            }
            if (!totalKibibytes.HasValue || !availableKibibytes.HasValue || availableKibibytes.Value > totalKibibytes.Value) return;
            sample.MemoryTotalBytes = totalKibibytes.Value * 1024L;
            sample.MemoryUsedBytes = (totalKibibytes.Value - availableKibibytes.Value) * 1024L;
        }

        /// <summary>
        /// Fill disk fields from a drive size and free space. A missing or inconsistent pair leaves both fields null.
        /// Equal size and free space records zero bytes used.
        /// </summary>
        /// <param name="sample">Sample to fill.</param>
        /// <param name="totalBytes">Drive size, or null.</param>
        /// <param name="availableBytes">Free bytes, or null.</param>
        public static void ReadDisk(HarborHostSample sample, long? totalBytes, long? availableBytes)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            if (!totalBytes.HasValue || !availableBytes.HasValue) return;
            if (totalBytes.Value < 0 || availableBytes.Value < 0 || availableBytes.Value > totalBytes.Value) return;
            sample.DiskTotalBytes = totalBytes.Value;
            sample.DiskUsedBytes = totalBytes.Value - availableBytes.Value;
        }

        private static bool TryReadCpuCounters(string? line, out long idle, out long total)
        {
            idle = 0;
            total = 0;
            if (String.IsNullOrWhiteSpace(line)) return false;
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !String.Equals(parts[0], "cpu", StringComparison.Ordinal)) return false;
            int count = Math.Min(8, parts.Length - 1);
            if (count < 4) return false;
            long[] values = new long[8];
            for (int index = 0; index < count; index++)
            {
                if (!Int64.TryParse(parts[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[index])) return false;
                if (values[index] < 0) return false;
            }
            idle = values[3] + (count > 4 ? values[4] : 0L);
            for (int index = 0; index < count; index++) total += values[index];
            return total >= idle;
        }

        private static long? Kibibytes(string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            if (!Int64.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value < 0) return null;
            return value;
        }
    }
}
