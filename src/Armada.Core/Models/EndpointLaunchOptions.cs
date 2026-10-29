namespace Armada.Core.Models
{
    using System.Collections.Generic;

    /// <summary>
    /// Options applied when a mission or Ask turn launches on an inference endpoint.
    /// A stored null reads as these defaults, which keep the launch behavior an endpoint had
    /// before the options existed. An empty allowed-tool list allows no tools. A null list
    /// allows every tool the launch would otherwise offer.
    /// </summary>
    public sealed class EndpointLaunchOptions
    {
        #region Public-Members

        /// <summary>The iteration cap used when a launch does not set one.</summary>
        public const int DefaultMaxIterations = 100;

        /// <summary>The largest iteration cap a launch accepts.</summary>
        public const int MaxIterationLimit = 1000;

        /// <summary>The largest completion length a launch accepts, when one is set.</summary>
        public const int MaxTokenLimit = 10000000;

        /// <summary>The longest accepted tool name.</summary>
        public const int MaxToolNameLength = 256;

        /// <summary>
        /// Whether the launch offers tools. Defaults to true.
        /// </summary>
        public bool ToolsEnabled { get; set; } = true;

        /// <summary>
        /// Whether the launch offers only built-in read-only tools. A read-only launch does not
        /// offer shell, file mutations, or MCP tools. Defaults to false.
        /// </summary>
        public bool ReadOnlyTools { get; set; } = false;

        /// <summary>
        /// Exact tool names the launch may offer. Null allows every tool the launch would otherwise
        /// offer. An empty list allows none.
        /// </summary>
        public List<string>? AllowedTools { get; set; } = null;

        /// <summary>
        /// Completion length for each model request. Null leaves the provider default in place.
        /// </summary>
        public int? MaxTokens { get; set; } = null;

        /// <summary>
        /// Most tool-calling iterations the launch runs. Defaults to <see cref="DefaultMaxIterations"/>.
        /// </summary>
        public int MaxIterations { get; set; } = DefaultMaxIterations;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Copy the options, including the allowed-tool list, so a running launch keeps the values it started with.
        /// </summary>
        /// <returns>A copy.</returns>
        public EndpointLaunchOptions Copy()
        {
            return new EndpointLaunchOptions
            {
                ToolsEnabled = ToolsEnabled,
                ReadOnlyTools = ReadOnlyTools,
                AllowedTools = AllowedTools == null ? null : new List<string>(AllowedTools),
                MaxTokens = MaxTokens,
                MaxIterations = MaxIterations
            };
        }

        #endregion
    }
}
