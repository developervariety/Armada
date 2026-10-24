namespace Armada.Runtimes.Harbor
{
    using System;
    using System.Linq;

    /// <summary>Local job journal identity operations.</summary>
    public sealed partial class HarborJobJournal
    {
        private sealed class StopRequest
        {
            public StopRequest() { }

            public string JobId { get; set; } = String.Empty;
            public string LaunchKey { get; set; } = String.Empty;
            public int GracePeriodMs { get; set; }
            public int? ProcessId { get; set; }
            public DateTime? ProcessStartedUtc { get; set; }
            public long? ProcessLinuxStartTicks { get; set; }
            public string? ProcessLinuxBootId { get; set; }
        }

        private static void ValidateJobId(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(character => !(Char.IsAsciiLetterOrDigit(character) || character == '-' || character == '_')))
                throw Failure("job_id_invalid", "The job identifier has an invalid format.");
        }

        private static void ValidateLaunchKey(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(Char.IsControl))
                throw Failure("launch_key_invalid", "The launch key has an invalid format.");
        }
    }
}
