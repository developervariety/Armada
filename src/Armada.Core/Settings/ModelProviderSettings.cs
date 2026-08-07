namespace Armada.Core.Settings
{
    using System;

    /// <summary>
    /// Connection and credential defaults for one external model provider (for example
    /// Zyloo or cun-ai) that serves captain models outside the native runtimes.
    /// </summary>
    /// <remarks>
    /// A provider is identified by the namespace prefix of a captain model id, for
    /// example <c>zyloo/claude-fable-5</c> names the <c>zyloo</c> provider and model
    /// <c>claude-fable-5</c>. A registered provider lets a captain route to that
    /// endpoint without repeating the base URL or key on every captain record.
    /// </remarks>
    public class ModelProviderSettings
    {
        #region Public-Members

        /// <summary>
        /// Human-readable display name for the provider, for example "Zyloo".
        /// </summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>
        /// Anthropic-native base URL used by the Claude Code runtime. The Claude CLI
        /// appends <c>/v1/messages</c> itself, so this must omit the <c>/v1</c> segment.
        /// </summary>
        public string BaseUrl { get; set; } = String.Empty;

        /// <summary>
        /// OpenAI-compatible base URL used by the OpenCode provider overlay, when the
        /// provider serves one. Differs from <see cref="BaseUrl"/> because the OpenCode
        /// overlay speaks the OpenAI-compatible adapter protocol.
        /// </summary>
        public string OpenAiBaseUrl { get; set; } = String.Empty;

        /// <summary>
        /// Name of the host environment variable that holds this provider's default API
        /// key. A captain's own <c>ApiKey</c> wins over this fallback when both exist.
        /// </summary>
        public string ApiKeyEnv { get; set; } = String.Empty;

        #endregion
    }
}
