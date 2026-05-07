namespace Armada.Server.Mcp
{
    /// <summary>
    /// MCP tool arguments for creating a captain.
    /// </summary>
    public class CaptainCreateArgs
    {
        /// <summary>
        /// Captain display name.
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Agent runtime: ClaudeCode, Codex, Gemini, Cursor, Mux, or Custom.
        /// </summary>
        public string? Runtime { get; set; }

        /// <summary>
        /// System instructions for this captain. Injected into every mission prompt.
        /// </summary>
        public string? SystemInstructions { get; set; }

        /// <summary>
        /// AI model identifier. Null means the runtime selects its default model.
        /// </summary>
        public string? Model { get; set; }

        /// <summary>
        /// JSON array of persona names this captain can fill. Null means any persona.
        /// </summary>
        public string? AllowedPersonas { get; set; }

        /// <summary>
        /// Preferred persona for dispatch routing priority.
        /// </summary>
        public string? PreferredPersona { get; set; }

        /// <summary>
        /// Optional Mux config directory override.
        /// </summary>
        public string? MuxConfigDirectory { get; set; }

        /// <summary>
        /// Named Mux endpoint to use for this captain.
        /// </summary>
        public string? MuxEndpoint { get; set; }

        /// <summary>
        /// Optional Mux base URL override.
        /// </summary>
        public string? MuxBaseUrl { get; set; }

        /// <summary>
        /// Optional Mux adapter type override.
        /// </summary>
        public string? MuxAdapterType { get; set; }

        /// <summary>
        /// Optional Mux temperature override.
        /// </summary>
        public double? MuxTemperature { get; set; }

        /// <summary>
        /// Optional Mux max tokens override.
        /// </summary>
        public int? MuxMaxTokens { get; set; }

        /// <summary>
        /// Optional Mux system prompt file path.
        /// </summary>
        public string? MuxSystemPromptPath { get; set; }

        /// <summary>
        /// Optional Mux approval policy override.
        /// </summary>
        public string? MuxApprovalPolicy { get; set; }

        /// <summary>
        /// Reasoning-effort / thinking-budget tier forwarded to the runtime CLI.
        /// Codex, ClaudeCode, and Cursor accept low|medium|high.
        /// Null means use the runtime's CLI default.
        /// </summary>
        public string? ReasoningEffort { get; set; }

    }
}
