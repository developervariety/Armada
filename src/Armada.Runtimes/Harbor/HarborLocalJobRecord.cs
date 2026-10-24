namespace Armada.Runtimes.Harbor
{
    using System;

    /// <summary>Durable, non-secret identity and state for one local Harbor job.</summary>
    public sealed class HarborLocalJobRecord
    {
        /// <summary>Harbor job identifier.</summary>
        public string JobId { get; set; } = String.Empty;
        /// <summary>Server-issued launch key.</summary>
        public string LaunchKey { get; set; } = String.Empty;
        /// <summary>Supervisor process identifier.</summary>
        public int SupervisorProcessId { get; set; }
        /// <summary>UTC time when the supervisor started.</summary>
        public DateTime SupervisorStartedUtc { get; set; }
        /// <summary>Exact Linux kernel process start tick for the supervisor.</summary>
        public long? SupervisorLinuxStartTicks { get; set; }
        /// <summary>Linux boot identifier for the supervisor process.</summary>
        public string? SupervisorLinuxBootId { get; set; }
        /// <summary>Child process identifier, when the child started.</summary>
        public int? ProcessId { get; set; }
        /// <summary>Normalized UTC child start time, when known.</summary>
        public DateTime? ProcessStartedUtc { get; set; }
        /// <summary>Exact Linux kernel process start tick for the child.</summary>
        public long? ProcessLinuxStartTicks { get; set; }
        /// <summary>Linux boot identifier for the child process.</summary>
        public string? ProcessLinuxBootId { get; set; }
        /// <summary>Whether the child is known to be running.</summary>
        public bool IsRunning { get; set; }
        /// <summary>Whether the child exit is confirmed.</summary>
        public bool StopConfirmed { get; set; }
        /// <summary>Known child exit code.</summary>
        public int? ExitCode { get; set; }
        /// <summary>Named launch or supervision failure.</summary>
        public string? FailureReason { get; set; }
        /// <summary>First retained output sequence.</summary>
        public long FirstOutputSequence { get; set; }
        /// <summary>Next output sequence to append.</summary>
        public long NextOutputSequence { get; set; }
        /// <summary>Whether output is incomplete and cannot support contiguous replay.</summary>
        public bool OutputGap { get; set; }
    }
}
