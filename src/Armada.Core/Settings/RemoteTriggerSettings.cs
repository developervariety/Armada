namespace Armada.Core.Settings
{
    using System.Collections.Generic;

    /// <summary>Transport mode for RemoteTriggerService.</summary>
    public enum RemoteTriggerMode
    {
        /// <summary>All wake calls are no-ops. Explicit opt-out regardless of Enabled flag.</summary>
        Disabled,

        /// <summary>HTTP POST to Claude Code Routines /fire endpoint. Default for backward compatibility.</summary>
        RemoteFire,

        /// <summary>Spawn a local subprocess (e.g. claude CLI) with event text piped to stdin.</summary>
        LocalDaemon,
    }

    /// <summary>Settings for spawning a local daemon process in <see cref="RemoteTriggerMode.LocalDaemon"/> mode.</summary>
    public sealed class LocalDaemonSettings
    {
        /// <summary>Executable to run (e.g. "claude"). Required for LocalDaemon mode.</summary>
        public string? Command { get; set; }

        /// <summary>Command-line arguments appended after the executable (e.g. "--dangerously-skip-permissions --print").</summary>
        public string Args { get; set; } = "";

        /// <summary>Orchestrator system prompt prepended before the event text. The event text is appended after a blank line.</summary>
        public string PromptTemplate { get; set; } = "";

        /// <summary>Working directory for the spawned process. Null inherits the admiral process working directory.</summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>Maximum seconds to allow the process to run before killing it. Default 600.</summary>
        public int TimeoutSeconds { get; set; } = 600;

        /// <summary>Additional environment variables merged into the spawned process environment.</summary>
        public Dictionary<string, string>? EnvironmentVariables { get; set; }
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

        /// <summary>Settings for LocalDaemon mode. Required when Mode is LocalDaemon.</summary>
        public LocalDaemonSettings? LocalDaemon { get; set; }

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

        /// <summary>Returns true if the section has the minimum config to spawn a local daemon process.</summary>
        public bool IsLocalDaemonConfigured()
        {
            return Enabled
                && Mode == RemoteTriggerMode.LocalDaemon
                && LocalDaemon != null
                && !string.IsNullOrWhiteSpace(LocalDaemon.Command);
        }
    }
}
