namespace Armada.Runtimes.Harbor
{
    /// <summary>Bounded local process result used by workspace operations.</summary>
    internal sealed class HarborWorkspaceProcessResult
    {
        /// <summary>Child exit code when the process exited and supplied one.</summary>
        public int? ExitCode { get; set; }
        /// <summary>Bounded decoded stdout retained for diagnostics.</summary>
        public string StandardOutput { get; set; } = System.String.Empty;
        /// <summary>Bounded decoded stderr retained for diagnostics.</summary>
        public string StandardError { get; set; } = System.String.Empty;
        /// <summary>Retained stdout bytes written to the output journal.</summary>
        public byte[] StandardOutputBytes { get; set; } = System.Array.Empty<byte>();
        /// <summary>Retained stderr bytes written to the output journal.</summary>
        public byte[] StandardErrorBytes { get; set; } = System.Array.Empty<byte>();
        /// <summary>Total stdout bytes read from the child.</summary>
        public long StandardOutputTotalBytes { get; set; }
        /// <summary>Total stderr bytes read from the child.</summary>
        public long StandardErrorTotalBytes { get; set; }
        /// <summary>Stdout bytes omitted after reaching its retention limit.</summary>
        public long StandardOutputOmittedBytes { get; set; }
        /// <summary>Stderr bytes omitted after reaching its retention limit.</summary>
        public long StandardErrorOmittedBytes { get; set; }
        /// <summary>Whether captured output exceeded a retained stream limit.</summary>
        public bool Truncated { get; set; }
        /// <summary>Whether the configured process deadline expired.</summary>
        public bool TimedOut { get; set; }
        /// <summary>Whether the operation token requested cancellation.</summary>
        public bool Cancelled { get; set; }
        /// <summary>Whether process exit was confirmed after a stop request.</summary>
        public bool StopConfirmed { get; set; }
        /// <summary>Elapsed process duration in milliseconds.</summary>
        public long DurationMilliseconds { get; set; }
        /// <summary>UTC time when the child process started.</summary>
        public DateTime StartedUtc { get; set; }
    }
}
