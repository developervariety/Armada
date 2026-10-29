namespace Armada.Runtimes
{
    using System;
    using System.Collections.Generic;
    using Armada.Core.Models;
    using Armada.Runtimes.Tools;

    /// <summary>
    /// Which tools one launch may offer and run. Filtering the offered list is not enough: a model
    /// can still name a tool that was left out, and that call is refused rather than executed.
    /// </summary>
    internal static class EndpointLaunchPolicy
    {
        #region Public-Methods

        /// <summary>
        /// Whether the launch offers any tool at all.
        /// </summary>
        /// <param name="options">Launch options. Null allows tools.</param>
        /// <returns>False when tools are off or the allow-list is empty.</returns>
        internal static bool OffersTools(EndpointLaunchOptions? options)
        {
            if (options == null) return true;
            if (!options.ToolsEnabled) return false;
            if (options.AllowedTools != null && options.AllowedTools.Count == 0) return false;
            return true;
        }

        /// <summary>
        /// Whether a built-in tool may be offered and executed.
        /// </summary>
        /// <param name="options">Launch options.</param>
        /// <param name="name">Tool name.</param>
        /// <param name="kind">Mutation kind of the built-in tool.</param>
        /// <returns>True when the launch allows the tool.</returns>
        internal static bool AllowsBuiltIn(EndpointLaunchOptions? options, string name, ToolMutationKind kind)
        {
            if (!OffersTools(options)) return false;
            if (options != null && options.ReadOnlyTools && kind != ToolMutationKind.ReadOnly) return false;
            return NameAllowed(options, name);
        }

        /// <summary>
        /// Whether an MCP tool may be offered and executed. A read-only launch allows no MCP tool,
        /// because an unclassified remote tool is treated as mutating.
        /// </summary>
        /// <param name="options">Launch options.</param>
        /// <param name="name">Tool name.</param>
        /// <returns>True when the launch allows the tool.</returns>
        internal static bool AllowsMcp(EndpointLaunchOptions? options, string name)
        {
            if (!OffersTools(options)) return false;
            if (options != null && options.ReadOnlyTools) return false;
            return NameAllowed(options, name);
        }

        /// <summary>
        /// The allowed names, or null when every otherwise permitted name is allowed.
        /// </summary>
        /// <param name="options">Launch options.</param>
        /// <returns>A set of exact names, or null.</returns>
        internal static HashSet<string>? AllowedNames(EndpointLaunchOptions? options)
        {
            if (options?.AllowedTools == null) return null;
            return new HashSet<string>(options.AllowedTools, StringComparer.Ordinal);
        }

        #endregion

        #region Private-Methods

        private static bool NameAllowed(EndpointLaunchOptions? options, string name)
        {
            if (options?.AllowedTools == null) return true;
            foreach (string allowed in options.AllowedTools)
            {
                if (String.Equals(allowed, name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        #endregion
    }
}
