namespace Armada.Core.Services.Interfaces
{
    using System;
    using Armada.Core.Models;

    /// <summary>
    /// Abstraction for spawning a one-shot agent process (Claude or Codex) for AgentWake events.
    /// The implementation must monitor the process in the background and invoke <c>onExited</c>
    /// when the process exits, times out, or can no longer be monitored.
    /// </summary>
    public interface IAgentWakeProcessHost
    {
        /// <summary>
        /// Attempts to start the agent process described by <paramref name="request"/>.
        /// On successful spawn, the implementation monitors the process in a background task and
        /// calls <paramref name="onExited"/> when it finishes (normal exit, timeout kill, or error).
        /// Returns <c>true</c> on successful spawn; returns <c>false</c> on spawn failure and does
        /// NOT call <paramref name="onExited"/> in that case.
        /// </summary>
        bool TryStart(AgentWakeProcessRequest request, Action onExited);
    }
}
