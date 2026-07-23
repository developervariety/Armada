namespace Armada.Core.Services
{
    using System;

    /// <summary>
    /// Shared timeout resolver for admiral-invoked git processes.
    /// Centralizes ARMADA_GIT_TIMEOUT_MS env-var parsing and clamping so the bound applied to
    /// every git invocation is configurable rather than a hardcoded literal.
    /// Deliberately lives in Armada.Core.Services (not Armada.Server.Mcp.Tools) because Core
    /// cannot depend on Server; it mirrors the shape of CodeContextTimeouts.Resolve.
    /// </summary>
    public static class GitProcessTimeouts
    {
        #region Public-Members

        /// <summary>
        /// Environment variable name that overrides the default timeout for all git invocations.
        /// </summary>
        public const string TimeoutEnvVar = "ARMADA_GIT_TIMEOUT_MS";

        /// <summary>
        /// Default timeout for a single git process (120 seconds). Clone/push/fetch of large repos
        /// over slow connections can easily exceed 30 seconds, especially in CI or containers.
        /// </summary>
        public const int DefaultTimeoutMs = 120_000;

        /// <summary>
        /// Minimum accepted timeout in milliseconds. Guards against a misconfigured env var
        /// making every git call fail instantly.
        /// </summary>
        public const int MinTimeoutMs = 1_000;

        /// <summary>
        /// Maximum accepted timeout in milliseconds (10 minutes). Guards against a misconfigured
        /// env var reintroducing an effectively unbounded wait.
        /// </summary>
        public const int MaxTimeoutMs = 600_000;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Resolve the git process timeout from <see cref="TimeoutEnvVar"/>, falling back to
        /// <see cref="DefaultTimeoutMs"/> when the variable is absent, unparseable, or non-positive.
        /// The resolved value is clamped to [<see cref="MinTimeoutMs"/>, <see cref="MaxTimeoutMs"/>].
        /// </summary>
        /// <returns>The timeout to apply to a single git invocation.</returns>
        public static TimeSpan Resolve()
        {
            string? configured = Environment.GetEnvironmentVariable(TimeoutEnvVar);
            if (Int32.TryParse(configured, out int milliseconds) && milliseconds > 0)
                return TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, MinTimeoutMs, MaxTimeoutMs));
            return TimeSpan.FromMilliseconds(DefaultTimeoutMs);
        }

        #endregion
    }
}
