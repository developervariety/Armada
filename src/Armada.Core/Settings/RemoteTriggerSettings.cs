namespace Armada.Core.Settings
{
    using System.Text.Json.Serialization;

    /// <summary>Transport mode for RemoteTriggerService.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RemoteTriggerMode
    {
        /// <summary>All wake calls are no-ops. Explicit opt-out regardless of Enabled flag.</summary>
        Disabled,

        /// <summary>HTTP POST to Claude Code Routines /fire endpoint. Default for backward compatibility.</summary>
        RemoteFire,

        /// <summary>Spawn a local Claude or Codex process on wake events. No HTTP required.</summary>
        AgentWake,
    }

    /// <summary>
    /// Configuration for admiral-side event-driven orchestrator wakes. Lives under the
    /// <c>remoteTrigger</c> key in ~/.armada/settings.json.
    /// </summary>
    public sealed class RemoteTriggerSettings
    {
        /// <summary>Master enable; false (or section absent) disables the feature entirely.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Transport mode. Defaults to <see cref="RemoteTriggerMode.RemoteFire"/> for backward
        /// compatibility with existing configs that omit this field.
        /// </summary>
        public RemoteTriggerMode Mode { get; set; } = RemoteTriggerMode.RemoteFire;

        /// <summary>Per-routine fire URL from the claude.ai/code/routines API trigger modal.</summary>
        public string? DrainerFireUrl { get; set; }

        /// <summary>Bearer token for the drainer routine; one-time-shown in the API trigger modal.</summary>
        public string? DrainerBearerToken { get; set; }

        /// <summary>Optional separate fire URL for audit-Critical events. Null routes Critical events through DrainerFireUrl with a [CRITICAL] text prefix.</summary>
        public string? CriticalFireUrl { get; set; }

        /// <summary>Bearer token for the critical routine. Required when CriticalFireUrl is set.</summary>
        public string? CriticalBearerToken { get; set; }

        /// <summary>Anthropic beta header value. User-configurable so future migrations do not require admiral rebuild.</summary>
        public string BetaHeader { get; set; } = "experimental-cc-routine-2026-04-01";

        /// <summary>Anthropic API version header value.</summary>
        public string AnthropicVersion { get; set; } = "2023-06-01";

        /// <summary>
        /// Maximum wake spawns per rolling hour (global, across all vessels). Defaults to 20.
        /// Raise for burst workloads where many WorkProduced events arrive faster than wakes drain.
        /// Values less than or equal to zero fall back to 20.
        /// </summary>
        public int ThrottleCapPerHour { get; set; } = 20;

        /// <summary>
        /// AgentWake mode settings. Used when <see cref="Mode"/> is <see cref="RemoteTriggerMode.AgentWake"/>.
        /// If absent, defaults are used (Claude runtime, --continue, 600s timeout).
        /// </summary>
        public AgentWakeSettings? AgentWake { get; set; }

        /// <summary>Returns true if the section is configured for AgentWake mode (Enabled=true and Mode=AgentWake). AgentWake works with default settings.</summary>
        public bool IsAgentWakeConfigured()
        {
            return Enabled && Mode == RemoteTriggerMode.AgentWake;
        }

        /// <summary>Returns true if the section has the minimum config to fire drainer wakes via RemoteFire mode.</summary>
        public bool IsDrainerConfigured()
        {
            return Enabled
                && Mode == RemoteTriggerMode.RemoteFire
                && !string.IsNullOrEmpty(DrainerFireUrl)
                && !string.IsNullOrEmpty(DrainerBearerToken)
                && !string.IsNullOrEmpty(BetaHeader)
                && !string.IsNullOrEmpty(AnthropicVersion);
        }

        /// <summary>Returns true if the section has the minimum config to fire critical wakes via a dedicated route.</summary>
        public bool IsCriticalConfigured()
        {
            return Enabled
                && Mode == RemoteTriggerMode.RemoteFire
                && !string.IsNullOrEmpty(CriticalFireUrl)
                && !string.IsNullOrEmpty(CriticalBearerToken)
                && !string.IsNullOrEmpty(BetaHeader)
                && !string.IsNullOrEmpty(AnthropicVersion);
        }
    }
}
