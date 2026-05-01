namespace Armada.Core.Models
{
    /// <summary>
    /// Unified runtime-options payload persisted in <see cref="Captain.RuntimeOptionsJson"/>.
    /// Composes runtime-agnostic settings (e.g. <see cref="ReasoningEffort"/>) with
    /// runtime-specific settings (Mux endpoint config, future Codex/Claude/Cursor knobs).
    /// Flat schema (Approach A): all keys at the root, ignored by runtimes that don't use them.
    /// </summary>
    public class CaptainOptions
    {
        #region Public-Members

        /// <summary>
        /// Schema version for the serialized options payload.
        /// </summary>
        public int SchemaVersion { get; set; } = 1;

        /// <summary>
        /// Reasoning effort / thinking-budget tier passed to the underlying runtime CLI.
        /// Valid values depend on the captain's runtime:
        /// Codex accepts low|medium|high|xhigh; Anthropic ClaudeCode accepts low|medium|high|xhigh|max.
        /// Cursor's accepted set depends on cursor-agent CLI version.
        /// Null means use the runtime's CLI default (no flag forwarded).
        /// </summary>
        public string? ReasoningEffort
        {
            get => _ReasoningEffort;
            set => _ReasoningEffort = Normalize(value);
        }

        /// <summary>
        /// Optional Mux config directory override.
        /// </summary>
        public string? ConfigDirectory
        {
            get => _ConfigDirectory;
            set => _ConfigDirectory = Normalize(value);
        }

        /// <summary>
        /// Named Mux endpoint to launch and validate against.
        /// </summary>
        public string? Endpoint
        {
            get => _Endpoint;
            set => _Endpoint = Normalize(value);
        }

        /// <summary>
        /// Optional Mux base URL override applied after endpoint selection.
        /// </summary>
        public string? BaseUrl
        {
            get => _BaseUrl;
            set => _BaseUrl = Normalize(value);
        }

        /// <summary>
        /// Optional Mux adapter type override applied after endpoint selection.
        /// </summary>
        public string? AdapterType
        {
            get => _AdapterType;
            set => _AdapterType = Normalize(value);
        }

        /// <summary>
        /// Optional Mux sampling temperature override.
        /// </summary>
        public double? Temperature { get; set; } = null;

        /// <summary>
        /// Optional Mux maximum token override.
        /// </summary>
        public int? MaxTokens { get; set; } = null;

        /// <summary>
        /// Optional Mux system prompt file path passed through to Mux.
        /// </summary>
        public string? SystemPromptPath
        {
            get => _SystemPromptPath;
            set => _SystemPromptPath = Normalize(value);
        }

        /// <summary>
        /// Optional Mux approval policy override (ask, auto, deny).
        /// </summary>
        public string? ApprovalPolicy
        {
            get => _ApprovalPolicy;
            set => _ApprovalPolicy = Normalize(value);
        }

        #endregion

        #region Private-Members

        private string? _ReasoningEffort = null;
        private string? _ConfigDirectory = null;
        private string? _Endpoint = null;
        private string? _BaseUrl = null;
        private string? _AdapterType = null;
        private string? _SystemPromptPath = null;
        private string? _ApprovalPolicy = null;

        #endregion

        #region Public-Methods

        /// <summary>
        /// Project this options payload into a Mux-only view (legacy helper compatibility).
        /// </summary>
        public MuxCaptainOptions ToMuxCaptainOptions()
        {
            return new MuxCaptainOptions
            {
                SchemaVersion = SchemaVersion,
                ConfigDirectory = ConfigDirectory,
                Endpoint = Endpoint,
                BaseUrl = BaseUrl,
                AdapterType = AdapterType,
                Temperature = Temperature,
                MaxTokens = MaxTokens,
                SystemPromptPath = SystemPromptPath,
                ApprovalPolicy = ApprovalPolicy
            };
        }

        /// <summary>
        /// Build a unified options instance from a legacy <see cref="MuxCaptainOptions"/>.
        /// </summary>
        public static CaptainOptions FromMuxCaptainOptions(MuxCaptainOptions mux)
        {
            return new CaptainOptions
            {
                SchemaVersion = mux.SchemaVersion,
                ConfigDirectory = mux.ConfigDirectory,
                Endpoint = mux.Endpoint,
                BaseUrl = mux.BaseUrl,
                AdapterType = mux.AdapterType,
                Temperature = mux.Temperature,
                MaxTokens = mux.MaxTokens,
                SystemPromptPath = mux.SystemPromptPath,
                ApprovalPolicy = mux.ApprovalPolicy
            };
        }

        #endregion

        #region Private-Methods

        private static string? Normalize(string? value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            return value.Trim();
        }

        #endregion
    }
}
