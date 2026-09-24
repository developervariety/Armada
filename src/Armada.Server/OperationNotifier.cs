namespace Armada.Server
{
    using System;
    using System.Threading.Tasks;
    using Armada.Core.Models;

    /// <summary>
    /// Where a shared operator operation reports what it changed: the event store (through the server's event
    /// emitter, which also forwards the event to WebSocket sessions and the remote tunnel) and the mission and voyage
    /// change broadcasts. The operation raises these itself, so REST, WebSocket and MCP report a change alike.
    /// </summary>
    public sealed class OperationNotifier
    {
        #region Public-Members

        /// <summary>
        /// A notifier that records nothing, for callers with no event store or WebSocket hub.
        /// </summary>
        public static OperationNotifier None => new OperationNotifier(null, null, null);

        #endregion

        #region Private-Members

        private readonly Func<string, string, string?, string?, string?, string?, string?, string?, Task>? _EmitEvent;
        private readonly Action<Mission>? _MissionChanged;
        private readonly Action<Voyage>? _VoyageChanged;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="emitEvent">Event emitter: type, message, entity type, entity id, captain, mission, vessel, voyage.</param>
        /// <param name="missionChanged">Mission change broadcast.</param>
        /// <param name="voyageChanged">Voyage change broadcast.</param>
        public OperationNotifier(
            Func<string, string, string?, string?, string?, string?, string?, string?, Task>? emitEvent,
            Action<Mission>? missionChanged,
            Action<Voyage>? voyageChanged)
        {
            _EmitEvent = emitEvent;
            _MissionChanged = missionChanged;
            _VoyageChanged = voyageChanged;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Write an event.
        /// </summary>
        /// <param name="eventType">Event type.</param>
        /// <param name="message">Event message.</param>
        /// <param name="entityType">Entity type.</param>
        /// <param name="entityId">Entity id.</param>
        /// <param name="captainId">Related captain.</param>
        /// <param name="missionId">Related mission.</param>
        /// <param name="vesselId">Related vessel.</param>
        /// <param name="voyageId">Related voyage.</param>
        public Task EmitAsync(
            string eventType,
            string message,
            string? entityType = null,
            string? entityId = null,
            string? captainId = null,
            string? missionId = null,
            string? vesselId = null,
            string? voyageId = null)
        {
            if (_EmitEvent == null) return Task.CompletedTask;
            return _EmitEvent(eventType, message, entityType, entityId, captainId, missionId, vesselId, voyageId);
        }

        /// <summary>
        /// Broadcast a changed mission to the sessions that may read it.
        /// </summary>
        /// <param name="mission">Changed mission.</param>
        public void MissionChanged(Mission mission)
        {
            if (mission == null) return;
            _MissionChanged?.Invoke(mission);
        }

        /// <summary>
        /// Broadcast a changed voyage to the sessions that may read it.
        /// </summary>
        /// <param name="voyage">Changed voyage.</param>
        public void VoyageChanged(Voyage voyage)
        {
            if (voyage == null) return;
            _VoyageChanged?.Invoke(voyage);
        }

        #endregion
    }
}
