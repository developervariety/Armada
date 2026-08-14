namespace Armada.Core.Services
{
    using System.Collections.Generic;

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
        /// CODEX_HOME) so the agent physically cannot read the host user's configuration.
        /// </summary>
        public Dictionary<string, string> EnvironmentOverrides { get; } = new Dictionary<string, string>();

        /// <summary>
        /// Configuration files to materialize under the scoped configuration directory before launch so
        /// the isolated agent still sees the Armada MCP server.
        /// </summary>
        public List<IsolationConfigFile> FilesToWrite { get; } = new List<IsolationConfigFile>();

        /// <summary>
        /// True when this plan carries no isolation steps (nothing to apply).
        /// </summary>
        public bool IsEmpty => ExtraArguments.Count == 0 && EnvironmentOverrides.Count == 0 && FilesToWrite.Count == 0;

        #endregion
    }
}
