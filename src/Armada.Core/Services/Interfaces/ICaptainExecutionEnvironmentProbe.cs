namespace Armada.Core.Services.Interfaces
{
    using System.Collections.Generic;

    /// <summary>
    /// Read-only view of the environment captains execute in. Every member inspects metadata only: nothing
    /// here may run a supplied binary, load it, or read secret material.
    /// </summary>
    public interface ICaptainExecutionEnvironmentProbe
    {
        /// <summary>Operating system of the captain environment: Linux, Windows, MacOS or Unknown.</summary>
        string OperatingSystem { get; }

        /// <summary>Process architecture of the captain environment, for example X64 or Arm64.</summary>
        string Architecture { get; }

        /// <summary>True when captains run inside a container boundary.</summary>
        bool IsContainer { get; }

        /// <summary>Names of licensed contexts available to captains.</summary>
        IReadOnlyCollection<string> LicensedContexts { get; }

        /// <summary>
        /// Resolve an executable by name on the captain PATH, or by an explicit path, without running it.
        /// </summary>
        /// <param name="executable">Executable name or path.</param>
        /// <returns>The resolved path, or null when it is not available.</returns>
        string? ResolveExecutable(string executable);

        /// <summary>Whether a file or directory exists in the captain environment.</summary>
        /// <param name="path">Absolute path.</param>
        /// <returns>True when the path exists.</returns>
        bool PathExists(string path);
    }
}
