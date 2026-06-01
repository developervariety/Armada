namespace Armada.Server.Mcp.Tools
{
    using System;
    using System.Text.Json;

    /// <summary>
    /// Shared timeout resolver for code-context operations.
    /// Centralizes ARMADA_CODE_CONTEXT_TIMEOUT_MS env-var parsing and clamping
    /// so dispatch-time and explicit armada_context_pack calls use the same logic.
    /// </summary>
    public static class CodeContextTimeouts
    {
        #region Public-Members

        /// <summary>
        /// Environment variable name that overrides the default timeout for all code-context operations.
        /// </summary>
        public const string TimeoutEnvVar = "ARMADA_CODE_CONTEXT_TIMEOUT_MS";

        /// <summary>
        /// Default timeout for dispatch-time context-pack generation (75 seconds).
        /// Raised from the original 15 seconds to accommodate index searches and summarization
        /// pipelines observed in production.
        /// </summary>
        public const int DefaultDispatchTimeoutMs = 75_000;

        /// <summary>
        /// Default timeout for explicit armada_context_pack / armada_fleet_context_pack tool calls (120 seconds).
        /// Interactive callers tolerate a longer wait; the env-var override applies here too.
        /// </summary>
        public const int DefaultExplicitTimeoutMs = 120_000;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Resolve the timeout from the environment variable, falling back to <paramref name="defaultMs"/>.
        /// Clamps the resolved value to [100, 300000] ms.
        /// </summary>
        /// <param name="defaultMs">Default milliseconds to use when the env var is absent or invalid.</param>
        public static TimeSpan Resolve(int defaultMs)
        {
            string? configured = Environment.GetEnvironmentVariable(TimeoutEnvVar);
            if (Int32.TryParse(configured, out int milliseconds) && milliseconds > 0)
                return TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 100, 300_000));
            return TimeSpan.FromMilliseconds(defaultMs);
        }

        /// <summary>
        /// Resolve the timeout for an explicit tool call, checking request args first,
        /// then the environment variable, then <see cref="DefaultExplicitTimeoutMs"/>.
        /// </summary>
        /// <param name="args">Tool call arguments JSON element, checked for a timeoutMs integer property.</param>
        public static TimeSpan ResolveForExplicitTool(JsonElement args)
        {
            if (args.TryGetProperty("timeoutMs", out JsonElement timeoutElement)
                && timeoutElement.ValueKind == JsonValueKind.Number
                && timeoutElement.TryGetInt32(out int requestedTimeout)
                && requestedTimeout > 0)
            {
                return TimeSpan.FromMilliseconds(Math.Clamp(requestedTimeout, 100, 300_000));
            }
            return Resolve(DefaultExplicitTimeoutMs);
        }

        #endregion
    }
}
