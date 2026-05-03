namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>Describes a process to spawn for an AgentWake event.</summary>
    public sealed class AgentWakeProcessRequest
    {
        /// <summary>Executable path or name (e.g. "claude" or "codex").</summary>
        public string Command { get; set; } = string.Empty;

        /// <summary>Ordered argument list passed via ProcessStartInfo.ArgumentList (no shell quoting needed).</summary>
        public List<string> ArgumentList { get; set; } = new List<string>();

        /// <summary>Payload written to the process stdin immediately after start, then stdin is closed.</summary>
        public string? StdinPayload { get; set; }

        /// <summary>Working directory for the spawned process. Null or empty uses the inherited directory.</summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>Seconds before the process is killed. Defaults to 600.</summary>
        public int TimeoutSeconds { get; set; } = 600;

        /// <summary>Optional additional environment variables for the spawned process.</summary>
        public Dictionary<string, string>? EnvironmentVariables { get; set; }
    }
}
