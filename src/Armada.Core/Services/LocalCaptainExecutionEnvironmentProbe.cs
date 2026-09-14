namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;

    /// <summary>
    /// Probes the environment captains are launched in. Captain runtimes start as child processes of the
    /// Admiral, so they share its operating system, architecture, container boundary and PATH. Executables
    /// are resolved by file lookup only and are never run.
    /// </summary>
    public sealed class LocalCaptainExecutionEnvironmentProbe : ICaptainExecutionEnvironmentProbe
    {
        #region Private-Members

        private readonly ArmadaSettings _Settings;
        private readonly string _PathVariable;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="settings">Armada settings supplying available licensed context names.</param>
        /// <param name="pathVariable">PATH to search; null uses the Admiral process PATH that captains inherit.</param>
        public LocalCaptainExecutionEnvironmentProbe(ArmadaSettings settings, string? pathVariable = null)
        {
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _PathVariable = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
        }

        #endregion

        #region Public-Members

        /// <inheritdoc />
        public string OperatingSystem
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "MacOS";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
                return "Unknown";
            }
        }

        /// <inheritdoc />
        public string Architecture => RuntimeInformation.ProcessArchitecture.ToString();

        /// <inheritdoc />
        public bool IsContainer =>
            File.Exists("/.dockerenv")
            || !String.IsNullOrEmpty(Environment.GetEnvironmentVariable("container"))
            || String.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

        /// <inheritdoc />
        public IReadOnlyCollection<string> LicensedContexts => _Settings.AvailableLicensedContexts.ToList();

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public string? ResolveExecutable(string executable)
        {
            if (String.IsNullOrWhiteSpace(executable)) return null;
            string name = executable.Trim();

            if (name.IndexOf(Path.DirectorySeparatorChar) >= 0 || name.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
                return File.Exists(name) ? Path.GetFullPath(name) : null;

            List<string> candidates = new List<string> { name };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && String.IsNullOrEmpty(Path.GetExtension(name)))
            {
                string extensions = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT";
                candidates.AddRange(extensions.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(ext => name + ext));
            }

            foreach (string directory in _PathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string candidate in candidates)
                {
                    string full = Path.Combine(directory.Trim(), candidate);
                    if (File.Exists(full)) return full;
                }
            }

            return null;
        }

        /// <inheritdoc />
        public bool PathExists(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            return File.Exists(path) || Directory.Exists(path);
        }

        #endregion
    }
}
