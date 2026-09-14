namespace Armada.Test.Unit.TestHelpers
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Scripted captain execution environment for dispatch preview tests.
    /// </summary>
    public sealed class FakeCaptainExecutionEnvironmentProbe : ICaptainExecutionEnvironmentProbe
    {
        /// <inheritdoc />
        public string OperatingSystem { get; set; } = "Linux";

        /// <inheritdoc />
        public string Architecture { get; set; } = "X64";

        /// <inheritdoc />
        public bool IsContainer { get; set; } = true;

        /// <summary>Licensed context names reported as available.</summary>
        public List<string> AvailableLicensedContexts { get; } = new List<string>();

        /// <inheritdoc />
        public IReadOnlyCollection<string> LicensedContexts => AvailableLicensedContexts;

        /// <summary>Executables that resolve.</summary>
        public HashSet<string> Executables { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Paths that exist.</summary>
        public HashSet<string> Paths { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <inheritdoc />
        public string? ResolveExecutable(string executable)
        {
            return Executables.Contains(executable) ? "/usr/bin/" + executable : null;
        }

        /// <inheritdoc />
        public bool PathExists(string path)
        {
            return Paths.Contains(path);
        }
    }
}
