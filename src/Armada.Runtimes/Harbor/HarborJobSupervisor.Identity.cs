namespace Armada.Runtimes.Harbor
{
    using System;

    /// <summary>Normalizes child process identity timestamps to provider-safe UTC precision.</summary>
    public sealed partial class HarborJobSupervisor
    {
        private static DateTime NormalizeUtc(DateTime value)
        {
            DateTime utc = value.ToUniversalTime();
            return new DateTime(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
        }
    }
}
