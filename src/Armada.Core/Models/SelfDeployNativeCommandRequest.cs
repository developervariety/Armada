namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A native database utility invocation with credentials supplied only through its environment.
    /// </summary>
    public sealed class SelfDeployNativeCommandRequest
    {
        /// <summary>Executable name or path.</summary>
        public string FileName { get; set; } = String.Empty;

        /// <summary>Arguments that contain no passwords or connection secrets.</summary>
        public IReadOnlyList<string> Arguments { get; set; } = Array.Empty<string>();

        /// <summary>Environment variables for the process, including provider password variables.</summary>
        public IReadOnlyDictionary<string, string> EnvironmentVariables { get; set; }
            = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Optional file whose contents are sent to standard input.</summary>
        public string? StandardInputFilePath { get; set; }

        /// <summary>Optional process working directory.</summary>
        public string? WorkingDirectory { get; set; }
    }
}
