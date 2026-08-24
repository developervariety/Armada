namespace Armada.Core.Models
{
    using Armada.Core.Settings;

    /// <summary>Effective AgentWake process-delivery configuration and transient session state.</summary>
    public sealed class AgentWakeStatusSnapshot
    {
        /// <summary>Whether remote trigger is enabled in AgentWake mode.</summary>
        public bool Configured { get; set; }

        /// <summary>Configured delivery mode.</summary>
        public AgentWakeDeliveryMode DeliveryMode { get; set; }

        /// <summary>Configured runtime before an Auto registration is applied.</summary>
        public AgentWakeRuntime Runtime { get; set; }

        /// <summary>Participant key stored in settings and retained across restarts.</summary>
        public string? ConfiguredParticipantKey { get; set; }

        /// <summary>Participant key currently accepted for addressed process wakes.</summary>
        public string? EffectiveParticipantKey { get; set; }

        /// <summary>Whether the delivery mode can start a process.</summary>
        public bool ProcessDeliveryEnabled { get; set; }

        /// <summary>Most recent transient orchestrator registration, when present.</summary>
        public AgentWakeSessionRegistration? Session { get; set; }
    }
}
