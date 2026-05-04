namespace Armada.Server.Mcp
{
    using Armada.Core.Settings;

    /// <summary>Arguments for registering a live orchestrator session for AgentWake Auto mode.</summary>
    public class AgentWakeSessionArgs
    {
        /// <summary>Concrete runtime: Codex or Claude.</summary>
        public AgentWakeRuntime Runtime { get; set; } = AgentWakeRuntime.Auto;

        /// <summary>Optional runtime-specific session identifier.</summary>
        public string? SessionId { get; set; }

        /// <summary>Optional command override for this runtime.</summary>
        public string? Command { get; set; }

        /// <summary>Optional working directory for the resumed orchestrator session.</summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>Optional client name/version for diagnostics.</summary>
        public string? ClientName { get; set; }
    }
}
