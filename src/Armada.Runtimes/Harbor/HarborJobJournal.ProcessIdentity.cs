namespace Armada.Runtimes.Harbor
{
    using System;

    /// <summary>Checks persisted process identities against current operating-system identity.</summary>
    public sealed partial class HarborJobJournal
    {
        /// <summary>Return true only when a live process has the exact recorded OS identity.</summary>
        /// <param name="processId">Operating-system process identifier.</param>
        /// <param name="startedUtc">UTC process start instant retained for cross-platform protocol identity.</param>
        /// <param name="linuxStartTicks">Linux kernel start tick, required on Linux.</param>
        /// <param name="linuxBootId">Linux boot UUID, required on Linux.</param>
        /// <returns>True when the process is alive and its exact OS identity matches.</returns>
        public static bool IsExactProcessAlive(int processId, DateTime? startedUtc, long? linuxStartTicks = null, string? linuxBootId = null)
        {
            if (processId <= 0 || !startedUtc.HasValue || startedUtc.Value.Kind != DateTimeKind.Utc) return false;
            try
            {
                using (System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId))
                {
                    if (process.HasExited) return false;
                    if (OperatingSystem.IsLinux())
                    {
                        if (!linuxStartTicks.HasValue || !Guid.TryParseExact(linuxBootId, "D", out _)) return false;
                        HarborLinuxProcessIdentity identity = HarborLinuxProcessIdentity.Read(processId);
                        return identity.StartTicks == linuxStartTicks.Value
                            && String.Equals(identity.BootId, linuxBootId, StringComparison.OrdinalIgnoreCase);
                    }
                    DateTime actual = NormalizeUtc(process.StartTime.ToUniversalTime());
                    return actual == startedUtc.Value;
                }
            }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException
                || exception is System.ComponentModel.Win32Exception || exception is NotSupportedException || exception is HarborJobJournalException)
            {
                return false;
            }
        }

        private static bool HasValidLinuxIdentity(long? startTicks, string? bootId)
        {
            if (!startTicks.HasValue && bootId == null) return true;
            return startTicks.HasValue && startTicks.Value > 0 && Guid.TryParseExact(bootId, "D", out _);
        }
    }
}
