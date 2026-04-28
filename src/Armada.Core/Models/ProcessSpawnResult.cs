namespace Armada.Core.Models
{
    /// <summary>Result returned by IProcessHost.SpawnDetachedAsync immediately after the process is launched.</summary>
    public sealed class ProcessSpawnResult
    {
        /// <summary>OS process ID assigned to the spawned process.</summary>
        public int ProcessId { get; set; }

        /// <summary>First up to 1 KB of stdout captured asynchronously; may be null if the process had not produced output before the result was returned.</summary>
        public string? StandardOutputTail { get; set; }

        /// <summary>True if the process had already exited by the time the result was constructed.</summary>
        public bool Exited { get; set; }
    }
}
