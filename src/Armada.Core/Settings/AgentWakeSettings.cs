namespace Armada.Core.Settings
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>Supported agent runtimes for AgentWake mode.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AgentWakeRuntime
    {
        /// <summary>Wake Claude Code CLI (default).</summary>
        Claude,

        /// <summary>Wake Codex CLI.</summary>
        Codex,
    }

    /// <summary>
    /// Configuration for AgentWake mode -- starts a local Claude or Codex process on wake events.
    /// Lives under the <c>remoteTrigger.agentWake</c> key in settings.json.
    /// Default values allow opt-in with only <c>mode: "AgentWake"</c> and no other fields.
    /// </summary>
    public sealed class AgentWakeSettings
    {
        /// <summary>Agent runtime to invoke. Defaults to <see cref="AgentWakeRuntime.Claude"/>.</summary>
        public AgentWakeRuntime Runtime { get; set; } = AgentWakeRuntime.Claude;

        /// <summary>
        /// Optional command override. If null or empty, defaults to "claude" for the Claude runtime
        /// or "codex" for the Codex runtime.
        /// </summary>
        public string? Command { get; set; }

        /// <summary>
        /// Optional session ID. If present, resumes the specified session; if absent,
        /// resumes the latest continuation candidate (--continue for Claude, --last for Codex).
        /// </summary>
        public string? SessionId { get; set; }

        /// <summary>Optional working directory for the spawned process. Null uses the process default.</summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>Seconds before the spawned agent process is killed. Defaults to 600 (10 minutes).</summary>
        public int TimeoutSeconds
        {
            get { return _TimeoutSeconds; }
            set { _TimeoutSeconds = value < 1 ? 1 : value; }
        }

        private int _TimeoutSeconds = 600;

        /// <summary>Optional environment variables added to the spawned process environment.</summary>
        public Dictionary<string, string>? EnvironmentVariables { get; set; }

        /// <summary>Returns the effective CLI command to invoke.</summary>
        public string GetEffectiveCommand()
        {
            if (!string.IsNullOrEmpty(Command)) return Command!;
            return Runtime == AgentWakeRuntime.Codex ? "codex" : "claude";
        }
    }
}
