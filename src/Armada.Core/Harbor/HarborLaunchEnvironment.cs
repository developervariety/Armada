namespace Armada.Core.Harbor
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Decides which launch environment variables may travel to a Harbor runner. A runner is another host: provider
    /// keys and base URLs, the Admiral's MCP launch credential and account logins never leave the Admiral. Only the
    /// named non-secret variables are forwarded; a launch whose runtime sets any other variable is refused with the
    /// variable names, and nothing is sent.
    /// </summary>
    public static class HarborLaunchEnvironment
    {
        #region Public-Members

        /// <summary>Refusal when a launch needs a variable that may not be sent to a runner.</summary>
        public const string ReasonEnvironmentUnsupported = "harbor_launch_environment_unsupported";

        /// <summary>Variables that may be sent to a runner.</summary>
        public static IReadOnlyCollection<string> ForwardedVariables => _Forwarded;

        #endregion

        #region Private-Members

        private static readonly HashSet<string> _Forwarded = new HashSet<string>(StringComparer.Ordinal)
        {
            "MSBUILDDISABLENODEREUSE",
            "DOTNET_CLI_USE_MSBUILD_SERVER",
            "CLAUDE_CODE_DISABLE_NONINTERACTIVE_HINT",
            "MAX_THINKING_TOKENS"
        };

        #endregion

        #region Public-Methods

        /// <summary>Select the variables a runner receives from the variables a launch sets.</summary>
        /// <param name="launchVariables">Variables the runtime set or changed for this launch.</param>
        /// <returns>The variables to send.</returns>
        /// <exception cref="HarborLaunchException">A variable may not be sent to a runner.</exception>
        public static Dictionary<string, string> Select(IReadOnlyDictionary<string, string> launchVariables)
        {
            if (launchVariables == null) throw new ArgumentNullException(nameof(launchVariables));
            Dictionary<string, string> selected = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> refused = new List<string>();
            foreach (KeyValuePair<string, string> variable in launchVariables)
            {
                if (_Forwarded.Contains(variable.Key)) selected[variable.Key] = variable.Value;
                else refused.Add(variable.Key);
            }
            if (refused.Count > 0)
            {
                refused.Sort(StringComparer.Ordinal);
                throw new HarborLaunchException(ReasonEnvironmentUnsupported, String.Join(", ", refused));
            }
            return selected;
        }

        #endregion
    }
}
