namespace Armada.Core.Services.Interfaces
{
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Orchestrates per-vessel coalescing + admiral-wide throttle + dual-routine routing
    /// (drainer vs critical) + consecutive-failure tracking with log fallback.
    /// Implementation reads RemoteTriggerSettings on construction; if not configured, all
    /// Fire* methods are no-ops.
    /// </summary>
    public interface IRemoteTriggerService
    {
        /// <summary>Fire a drainer wake for the given vessel with the given event-context text. Returns silently if coalesced, throttled, or disabled.</summary>
        Task FireDrainerAsync(string vesselId, string text, CancellationToken token = default);

        /// <summary>Fire a critical wake (audit Critical or similar). Bypasses coalescing and throttle since these are rare and high-priority.</summary>
        Task FireCriticalAsync(string text, CancellationToken token = default);

        /// <summary>Register the most recent orchestrator session used by AgentWake Auto mode.</summary>
        AgentWakeSessionRegistration RegisterAgentWakeSession(AgentWakeSessionRegistration registration);

        /// <summary>Return the most recently registered AgentWake orchestrator session, if any.</summary>
        AgentWakeSessionRegistration? GetAgentWakeSession();
    }
}
