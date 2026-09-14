namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Argument-list process launch used for supervised processes.
    /// </summary>
    public sealed class SelfDeployLaunchSpec
    {
        /// <summary>
        /// Executable.
        /// </summary>
        public string FileName { get; set; } = String.Empty;

        /// <summary>
        /// Arguments passed without shell parsing.
        /// </summary>
        public IReadOnlyList<string> Arguments { get; set; } = Array.Empty<string>();

        /// <summary>
        /// Working directory.
        /// </summary>
        public string WorkingDirectory { get; set; } = String.Empty;

        /// <summary>
        /// Extra environment variables.
        /// </summary>
        public IReadOnlyDictionary<string, string> EnvironmentVariables { get; set; }
            = new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
