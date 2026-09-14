namespace Armada.Core.Models
{
    using System;

    /// <summary>
    /// Result from an injected native database utility runner.
    /// </summary>
    public sealed class SelfDeployNativeCommandResult
    {
        /// <summary>Process exit code.</summary>
        public int ExitCode { get; set; }

        /// <summary>Captured standard output.</summary>
        public string StandardOutput { get; set; } = String.Empty;

        /// <summary>Captured standard error.</summary>
        public string StandardError { get; set; } = String.Empty;

        /// <summary>Whether the process completed successfully.</summary>
        public bool Succeeded => ExitCode == 0;
    }
}
