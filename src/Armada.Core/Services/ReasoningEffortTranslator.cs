namespace Armada.Core.Services
{
    using Armada.Core.Enums;

    /// <summary>
    /// Translates a captain's abstract <see cref="ReasoningEffortEnum"/> into each runtime's native
    /// reasoning control. Pure and side-effect free so it can be unit tested in isolation. Every method
    /// returns null when the level should produce no control (either the captain has no effort set, or
    /// the level maps to "leave the runtime default").
    /// </summary>
    public static class ReasoningEffortTranslator
    {
        #region Public-Methods

        /// <summary>
        /// The value for Mux's <c>--effort</c> flag: off | minimal | low | medium | high. Null when no
        /// effort is set (Mux keeps its endpoint default).
        /// </summary>
        /// <param name="effort">Captain reasoning effort, or null.</param>
        /// <returns>The Mux effort token, or null to omit the flag.</returns>
        public static string? ToMuxEffort(ReasoningEffortEnum? effort)
        {
            if (effort == null) return null;
            switch (effort.Value)
            {
                case ReasoningEffortEnum.Off: return "off";
                case ReasoningEffortEnum.Minimal: return "minimal";
                case ReasoningEffortEnum.Low: return "low";
                case ReasoningEffortEnum.Medium: return "medium";
                case ReasoningEffortEnum.High: return "high";
                default: return null;
            }
        }

        /// <summary>
        /// The value for Codex's <c>-c model_reasoning_effort=&lt;value&gt;</c>: minimal | low | medium | high.
        /// Null when no effort is set or when the level is Off (Codex has no explicit off, so omit).
        /// </summary>
        /// <param name="effort">Captain reasoning effort, or null.</param>
        /// <returns>The Codex reasoning-effort token, or null to omit the override.</returns>
        public static string? ToCodexReasoningEffort(ReasoningEffortEnum? effort)
        {
            if (effort == null) return null;
            switch (effort.Value)
            {
                case ReasoningEffortEnum.Off: return null;
                case ReasoningEffortEnum.Minimal: return "minimal";
                case ReasoningEffortEnum.Low: return "low";
                case ReasoningEffortEnum.Medium: return "medium";
                case ReasoningEffortEnum.High: return "high";
                default: return null;
            }
        }

        /// <summary>
        /// The value for Claude Code's <c>MAX_THINKING_TOKENS</c> environment variable. Null when no effort
        /// is set (Claude keeps its default). Off yields a minimal budget rather than default.
        /// </summary>
        /// <param name="effort">Captain reasoning effort, or null.</param>
        /// <returns>The thinking-token budget, or null to leave the env var unset.</returns>
        public static int? ToClaudeThinkingTokens(ReasoningEffortEnum? effort)
        {
            if (effort == null) return null;
            switch (effort.Value)
            {
                case ReasoningEffortEnum.Off: return 0;
                case ReasoningEffortEnum.Minimal: return 2048;
                case ReasoningEffortEnum.Low: return 4096;
                case ReasoningEffortEnum.Medium: return 8192;
                case ReasoningEffortEnum.High: return 16384;
                default: return null;
            }
        }

        /// <summary>
        /// The value for OpenCode's <c>--variant</c> flag (provider-specific reasoning effort). OpenCode
        /// exposes a coarser set (minimal / high / max), so the five Armada levels collapse onto those:
        /// Minimal and Low map to <c>minimal</c>, Medium and High map to <c>high</c>. Off and null omit the
        /// flag so OpenCode keeps its default.
        /// </summary>
        /// <param name="effort">Captain reasoning effort, or null.</param>
        /// <returns>The OpenCode variant token, or null to omit the flag.</returns>
        public static string? ToOpenCodeVariant(ReasoningEffortEnum? effort)
        {
            if (effort == null) return null;
            switch (effort.Value)
            {
                case ReasoningEffortEnum.Off: return null;
                case ReasoningEffortEnum.Minimal: return "minimal";
                case ReasoningEffortEnum.Low: return "minimal";
                case ReasoningEffortEnum.Medium: return "high";
                case ReasoningEffortEnum.High: return "high";
                default: return null;
            }
        }

        #endregion
    }
}
