namespace Armada.Core.Models
{
    using System;
    using Armada.Core.Settings;

    /// <summary>
    /// Last-seen orchestrator session used by AgentWake Auto mode.
    /// MCP clients can register this when an operator starts or resumes a Codex/Claude session.
    /// </summary>
    public sealed class AgentWakeSessionRegistration
    {
        /// <summary>Concrete runtime for the orchestrator session. Auto is not valid for registration.</summary>
        public AgentWakeRuntime Runtime { get; set; } = AgentWakeRuntime.Auto;

        /// <summary>Optional runtime-specific session identifier. Null uses --last/--continue fallback.</summary>
        public string? SessionId { get; set; }

        /// <summary>Optional command override for this registered runtime.</summary>
        public string? Command { get; set; }

        /// <summary>Optional working directory for the resumed orchestrator session.</summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>Optional client name/version for operator diagnostics.</summary>
        public string? ClientName { get; set; }

        /// <summary>UTC timestamp set by Armada when the registration is accepted.</summary>
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    }
}
