namespace Armada.Core.Services
{
    using System;
    using System.Net.Http;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Selects the code-index inference client (summarization and file signatures) from
    /// <c>codeIndex.inferenceClient</c>. Every process that builds a code-index service calls this
    /// one selection.
    /// </summary>
    public static class CodeIndexInferenceClientFactory
    {
        /// <summary>
        /// The inference-client mode that selects the OpenCode server client.
        /// </summary>
        public const string OpenCodeServerMode = "OpenCodeServer";

        /// <summary>
        /// Create the inference client for the configured mode. <c>OpenCodeServer</c> (any letter
        /// case) selects the OpenCode server client. Every other value, including <c>Http</c>, an
        /// empty value and an unknown value, selects the HTTP (DeepSeek-compatible) client. Creating
        /// a client makes no network call.
        /// </summary>
        /// <param name="settings">Armada settings.</param>
        /// <param name="logging">Logging module.</param>
        /// <param name="http">Shared HTTP client.</param>
        /// <returns>The selected inference client.</returns>
        public static IInferenceClient Create(ArmadaSettings settings, LoggingModule logging, HttpClient http)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (logging == null) throw new ArgumentNullException(nameof(logging));
            if (http == null) throw new ArgumentNullException(nameof(http));

            if (IsOpenCodeServerMode(settings.CodeIndex.InferenceClient))
            {
                return new OpenCodeServerInferenceClient(settings, logging, http);
            }

            return new DeepSeekInferenceClient(settings.CodeIndex, logging, http);
        }

        /// <summary>
        /// True when the inference-client mode selects the OpenCode server client.
        /// </summary>
        /// <param name="mode">Configured mode.</param>
        /// <returns>True for <c>OpenCodeServer</c> in any letter case.</returns>
        public static bool IsOpenCodeServerMode(string? mode)
        {
            return String.Equals(mode, OpenCodeServerMode, StringComparison.OrdinalIgnoreCase);
        }
    }
}
