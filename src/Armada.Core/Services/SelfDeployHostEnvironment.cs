namespace Armada.Core.Services
{
    using System;
    using System.IO;
    using Armada.Core.Services.Interfaces;

    /// <summary>
    /// Host facts read from the current process.
    /// </summary>
    public sealed class SelfDeployHostEnvironment : ISelfDeployHostEnvironment
    {
        /// <inheritdoc />
        public bool IsContainer
        {
            get
            {
                string? dotnetContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
                if (String.Equals(dotnetContainer, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (OperatingSystem.IsWindows()) return false;
                return File.Exists("/.dockerenv") || File.Exists("/run/.containerenv");
            }
        }

        /// <inheritdoc />
        public string CurrentServerDirectory => AppContext.BaseDirectory;

        /// <inheritdoc />
        public int CurrentProcessId => Environment.ProcessId;
    }
}
