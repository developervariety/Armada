namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;

    /// <summary>
    /// The environment every build or test command Armada starts carries, so no launcher can forget it: MSBuild
    /// worker-node reuse, the MSBuild server and the shared compiler server are off. A reused node or build server
    /// outlives the command that started it, keeps its memory, holds inherited pipes and lock descriptors open, and
    /// makes an idle host read as busy to any process check. MSBuild reads an environment variable as a property
    /// default, so a project that sets <c>UseSharedCompilation</c> itself keeps its own value.
    /// </summary>
    public static class BuildServerEnvironment
    {
        #region Public-Members

        /// <summary>Variable that turns off MSBuild worker-node reuse when set to <c>1</c>.</summary>
        public const string DisableNodeReuseVariable = "MSBUILDDISABLENODEREUSE";

        /// <summary>Variable that turns off the .NET CLI MSBuild server when set to <c>0</c>.</summary>
        public const string MsBuildServerVariable = "DOTNET_CLI_USE_MSBUILD_SERVER";

        /// <summary>Variable that turns off the shared compiler server when set to <c>false</c>.</summary>
        public const string SharedCompilationVariable = "UseSharedCompilation";

        /// <summary>The variables and the values a build or test command receives.</summary>
        public static IReadOnlyDictionary<string, string> Variables => _Variables;

        #endregion

        #region Private-Members

        private static readonly Dictionary<string, string> _Variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DisableNodeReuseVariable] = "1",
            [MsBuildServerVariable] = "0",
            [SharedCompilationVariable] = "false"
        };

        #endregion

        #region Public-Methods

        /// <summary>
        /// Set the variables on start settings. A launcher whose caller may choose its own values applies this
        /// first and the caller's variables after it.
        /// </summary>
        /// <param name="startInfo">Start settings to change.</param>
        public static void Apply(ProcessStartInfo startInfo)
        {
            if (startInfo == null) throw new ArgumentNullException(nameof(startInfo));
            Apply(startInfo.Environment);
        }

        /// <summary>
        /// Set the variables on a process environment.
        /// </summary>
        /// <param name="environment">Environment the child process will receive.</param>
        public static void Apply(IDictionary<string, string?> environment)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            foreach (KeyValuePair<string, string> variable in _Variables)
                environment[variable.Key] = variable.Value;
        }

        #endregion
    }
}
