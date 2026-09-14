namespace Armada.Core.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// What the captain's execution environment must provide before prepared research is dispatched. A
    /// readable file on the Admiral host does not prove a captain can load or run it, so each requirement
    /// is checked against the environment captains launch in. Only names and paths are recorded: never
    /// credentials, keys, license values or other secret material.
    /// </summary>
    public class ObjectiveExecutionRequirements
    {
        #region Public-Members

        /// <summary>Required operating system of the captain environment: Linux, Windows or MacOS.</summary>
        public string? OperatingSystem { get; set; } = null;

        /// <summary>Required process architecture of the captain environment, for example X64 or Arm64.</summary>
        public string? Architecture { get; set; } = null;

        /// <summary>Executables that must be resolvable on the captain PATH (a loader, an interpreter, a tool).</summary>
        public List<string> Executables { get; set; } = new List<string>();

        /// <summary>Files or directories the captain environment must be able to read.</summary>
        public List<string> DependencyPaths { get; set; } = new List<string>();

        /// <summary>Required isolation boundary around the captain: Container or Host.</summary>
        public string? IsolationBoundary { get; set; } = null;

        /// <summary>Name of a licensed context that must be available to captains. A name only, never license material.</summary>
        public string? LicensedContext { get; set; } = null;

        /// <summary>True when at least one requirement is declared.</summary>
        public bool HasAny =>
            !String.IsNullOrWhiteSpace(OperatingSystem)
            || !String.IsNullOrWhiteSpace(Architecture)
            || (Executables?.Any(item => !String.IsNullOrWhiteSpace(item)) ?? false)
            || (DependencyPaths?.Any(item => !String.IsNullOrWhiteSpace(item)) ?? false)
            || !String.IsNullOrWhiteSpace(IsolationBoundary)
            || !String.IsNullOrWhiteSpace(LicensedContext);

        #endregion
    }
}
