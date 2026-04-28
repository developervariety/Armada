namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>Describes a subprocess to launch via IProcessHost.</summary>
    public sealed class ProcessSpawnRequest
    {
        /// <summary>Executable name or full path (e.g. "claude").</summary>
        public string Command { get; set; } = "";

        /// <summary>Command-line arguments passed after the executable.</summary>
        public string Args { get; set; } = "";

        /// <summary>Text written to the process stdin. The process reads this as its input prompt.</summary>
        public string StdinPayload { get; set; } = "";

        /// <summary>Working directory for the spawned process. Null inherits the host process working directory.</summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>Seconds before the process monitor forcibly kills the process. Default 600.</summary>
        public int TimeoutSeconds { get; set; } = 600;

        /// <summary>Additional environment variables merged into the spawned process environment.</summary>
        public Dictionary<string, string>? EnvironmentVariables { get; set; }
    }
}
