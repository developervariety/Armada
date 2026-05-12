namespace Armada.Core.Settings
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    /// <summary>Supported agent runtimes for AgentWake mode.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AgentWakeRuntime
    {
        /// <summary>Resolve the runtime from a registered orchestrator session or configured fallback order.</summary>
        Auto,

        /// <summary>Wake Claude Code CLI (default).</summary>
        Claude,

        /// <summary>Wake Codex CLI.</summary>
        Codex,
    }

    /// <summary>
    /// Delivery channel for AgentWake events. Lets operators choose between
    /// the historical process-spawn behavior (AFK orchestrator) and an
    /// MCP-pollable signal channel (interactive orchestrator running in the
    /// same admiral instance).
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AgentWakeDeliveryMode
    {
        /// <summary>
        /// Spawn the agent CLI as a headless process (current default). Best when
        /// the operator is AFK with the interactive session closed; the spawned
        /// process resumes the saved conversation and drives the orchestrator turn
        /// autonomously.
        /// </summary>
        SpawnProcess,

        /// <summary>
        /// Write a <see cref="Armada.Core.Enums.SignalTypeEnum.Wake"/> signal row
        /// instead of spawning a process. Interactive operators drain these via
        /// <c>armada_enumerate entityType=signals signalType=Wake unreadOnly=true</c>
        /// and acknowledge with <c>armada_mark_signal_read</c>.
        /// </summary>
        McpNotification,

        /// <summary>
        /// Do both: spawn a process AND write a Wake signal. Useful for transition
        /// or belt-and-suspenders deployments; double-fires are documented as an
        /// opt-in race the operator accepts.
        /// </summary>
        Both,
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
        /// Runtime fallback order used only when <see cref="Runtime"/> is <see cref="AgentWakeRuntime.Auto"/>
        /// and no registered orchestrator session is available. Defaults to Codex, then Claude.
        /// </summary>
        public List<AgentWakeRuntime>? RuntimePreference { get; set; }

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

        /// <summary>
        /// Delivery mode for AgentWake events. Defaults to
        /// <see cref="AgentWakeDeliveryMode.SpawnProcess"/> for backward
        /// compatibility; interactive orchestrators should set this to
        /// <see cref="AgentWakeDeliveryMode.McpNotification"/>.
        /// </summary>
        public AgentWakeDeliveryMode DeliveryMode { get; set; } = AgentWakeDeliveryMode.SpawnProcess;

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
            AgentWakeRuntime runtime = Runtime == AgentWakeRuntime.Auto ? GetRuntimePreference()[0] : Runtime;
            return GetEffectiveCommand(runtime, Command);
        }

        /// <summary>Returns the effective CLI command to invoke for a concrete runtime.</summary>
        public string GetEffectiveCommand(AgentWakeRuntime runtime, string? commandOverride = null)
        {
            if (!string.IsNullOrEmpty(commandOverride)) return commandOverride!;
            if (!string.IsNullOrEmpty(Command)) return Command!;
            return runtime == AgentWakeRuntime.Codex ? "codex" : "claude";
        }

        /// <summary>Returns the configured Auto fallback order, excluding Auto itself.</summary>
        public List<AgentWakeRuntime> GetRuntimePreference()
        {
            List<AgentWakeRuntime> source = RuntimePreference == null || RuntimePreference.Count == 0
                ? new List<AgentWakeRuntime> { AgentWakeRuntime.Codex, AgentWakeRuntime.Claude }
                : RuntimePreference;

            List<AgentWakeRuntime> result = new List<AgentWakeRuntime>();
            foreach (AgentWakeRuntime runtime in source)
            {
                if (runtime == AgentWakeRuntime.Auto) continue;
                if (!result.Contains(runtime)) result.Add(runtime);
            }

            if (result.Count == 0)
            {
                result.Add(AgentWakeRuntime.Codex);
                result.Add(AgentWakeRuntime.Claude);
            }

            return result;
        }
    }
}
