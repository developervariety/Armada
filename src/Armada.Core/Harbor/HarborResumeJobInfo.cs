namespace Armada.Core.Harbor
{
    using System;

    /// <summary>One runner's durable view of a job attempt.</summary>
    public sealed class HarborResumeJobInfo
    {
        /// <summary>Server-issued job identifier.</summary>
        public string JobId { get; set; } = String.Empty;

        /// <summary>Launch key used by the Admiral for the attempt.</summary>
        public string LaunchKey { get; set; } = String.Empty;

        /// <summary>Operating-system process identifier on the runner.</summary>
        public int? ProcessId { get; set; }

        /// <summary>Operating-system process start time on the runner, in UTC.</summary>
        public DateTime? ProcessStartedUtc { get; set; }

        /// <summary>Whether the process is still running.</summary>
        public bool IsRunning { get; set; }

        /// <summary>Whether the runner has confirmed that the process stopped.</summary>
        public bool StopConfirmed { get; set; }

        /// <summary>Exit code, when the process stopped normally.</summary>
        public int? ExitCode { get; set; }

        /// <summary>First output sequence still available for replay.</summary>
        public long FirstAvailableOutputSequence { get; set; }

        /// <summary>Next output sequence after the last retained output.</summary>
        public long NextOutputSequence { get; set; }
    }
}
