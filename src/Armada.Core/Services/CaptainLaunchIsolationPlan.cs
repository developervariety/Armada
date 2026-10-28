namespace Armada.Core.Services
{
    using System.Collections.Generic;
    using Armada.Core.Enums;

    /// <summary>
    /// The concrete steps needed to launch a captain in an isolated agent configuration for one runtime:
    /// extra command-line arguments to append, environment variable overrides to apply, and scoped
    /// configuration files to write. An empty plan (all collections empty) means "no isolation applies"
    /// and the launch proceeds unchanged.
    /// </summary>
    public sealed class CaptainLaunchIsolationPlan
    {
        #region Public-Members

        /// <summary>
        /// Extra command-line arguments to append to the runtime's normal arguments (e.g. Claude's
        /// --strict-mcp-config). Empty when the runtime isolates purely via environment/config files.
        /// </summary>
        public List<string> ExtraArguments { get; } = new List<string>();

        /// <summary>
        /// Environment variable overrides to apply to the launched process (e.g. a scoped HOME or
        /// a scoped HOME) so the agent physically cannot read the host user's configuration.
        /// </summary>
        public Dictionary<string, string> EnvironmentOverrides { get; } = new Dictionary<string, string>();

        /// <summary>
        /// Configuration files to materialize under the scoped configuration directory before launch so
        /// the isolated agent still sees the Armada MCP server.
        /// </summary>
        public List<IsolationConfigFile> FilesToWrite { get; } = new List<IsolationConfigFile>();

        /// <summary>
        /// Native shell and file tool access of this launch. A plan is built per launch, so setting it here
        /// restricts that launch only and never the shared runtime settings. Mission launches leave the default.
        /// </summary>
        public NativeToolAccessEnum NativeToolAccess { get; set; } = NativeToolAccessEnum.Unrestricted;

        /// <summary>
        /// True when this plan carries no isolation steps (nothing to apply).
        /// </summary>
        public bool IsEmpty => ExtraArguments.Count == 0 && EnvironmentOverrides.Count == 0 && FilesToWrite.Count == 0
            && NativeToolAccess == NativeToolAccessEnum.Unrestricted;

        #endregion
    }
}
