namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Globalization;
    using System.IO;
    using System.Text;

    /// <summary>A kernel process identity that is stable across .NET processes on one Linux boot.</summary>
    internal sealed class HarborLinuxProcessIdentity
    {
        private HarborLinuxProcessIdentity(long startTicks, string bootId)
        {
            StartTicks = startTicks;
            BootId = bootId;
        }

        internal long StartTicks { get; }
        internal string BootId { get; }

        internal static HarborLinuxProcessIdentity Read(int processId)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux process identity is available only on Linux.");
            if (processId <= 0) throw new HarborJobJournalException("process_identity_invalid", "A positive process identifier is required.");
            try
            {
                string stat = ReadBounded($"/proc/{processId}/stat", 4096);
                int endName = stat.LastIndexOf(')');
                if (endName < 0 || endName + 1 >= stat.Length) throw InvalidIdentity();
                string[] fields = stat.Substring(endName + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length <= 19 || !Int64.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) || ticks <= 0)
                    throw InvalidIdentity();
                string bootId = ReadBounded("/proc/sys/kernel/random/boot_id", 64).Trim();
                if (!Guid.TryParseExact(bootId, "D", out _)) throw InvalidIdentity();
                return new HarborLinuxProcessIdentity(ticks, bootId.ToLowerInvariant());
            }
            catch (HarborJobJournalException) { throw; }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException
                || exception is DecoderFallbackException || exception is NotSupportedException)
            {
                throw new HarborJobJournalException("process_identity_unavailable", "The Linux kernel process identity could not be read.", exception);
            }
        }

        private static string ReadBounded(string path, int maximumBytes)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (stream.Length > maximumBytes) throw InvalidIdentity();
                byte[] bytes = new byte[maximumBytes + 1];
                int total = 0;
                while (total < bytes.Length)
                {
                    int count = stream.Read(bytes, total, bytes.Length - total);
                    if (count == 0) break;
                    total += count;
                }
                if (total > maximumBytes || stream.Length > maximumBytes) throw InvalidIdentity();
                return new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, total);
            }
        }

        private static HarborJobJournalException InvalidIdentity() => new HarborJobJournalException("process_identity_unavailable", "The Linux kernel returned an invalid process identity.");
    }
}
