namespace Armada.Core.Services
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// Records the dispatch hold's state changes as events, so a later reader sees who engaged, cleared,
    /// or inherited the hold and why. The hold itself lives in <see cref="DispatchHold"/>; these events
    /// are the history.
    /// </summary>
    public static class DispatchHoldEvents
    {
        #region Public-Members

        /// <summary>Event type recorded when the hold is engaged.</summary>
        public const string EngagedEventType = "dispatch.hold_engaged";

        /// <summary>Event type recorded when an active hold is cleared.</summary>
        public const string ClearedEventType = "dispatch.hold_cleared";

        /// <summary>Event type recorded when startup restores a stored hold.</summary>
        public const string RestoredEventType = "dispatch.hold_restored";

        /// <summary>Event type recorded when a vessel hold is engaged.</summary>
        public const string VesselEngagedEventType = "vessel.hold_engaged";

        /// <summary>Event type recorded when a vessel hold is cleared.</summary>
        public const string VesselClearedEventType = "vessel.hold_cleared";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record that the hold was engaged.
        /// </summary>
        /// <param name="database">Database driver whose Events collection receives the event.</param>
        /// <param name="hold">The engaged hold.</param>
        /// <param name="token">Cancellation token.</param>
        public static Task RecordEngagedAsync(DatabaseDriver database, DispatchHoldSnapshot hold, CancellationToken token = default)
        {
            if (hold == null) throw new ArgumentNullException(nameof(hold));
            string holder = String.IsNullOrWhiteSpace(hold.SetBy) ? "unknown" : hold.SetBy!;
            return RecordAsync(database, EngagedEventType,
                "Dispatch hold engaged by " + holder + ": " + hold.Reason,
                new { hold.Reason, hold.SetBy, hold.SetByUtc }, token);
        }

        /// <summary>
        /// Record that an active hold was cleared.
        /// </summary>
        /// <param name="database">Database driver whose Events collection receives the event.</param>
        /// <param name="cleared">The hold that was cleared.</param>
        /// <param name="clearedBy">Identity of the caller that cleared the hold, or null.</param>
        /// <param name="token">Cancellation token.</param>
        public static Task RecordClearedAsync(DatabaseDriver database, DispatchHoldSnapshot cleared, string? clearedBy, CancellationToken token = default)
        {
            if (cleared == null) throw new ArgumentNullException(nameof(cleared));
            string who = String.IsNullOrWhiteSpace(clearedBy) ? "unknown" : clearedBy!;
            return RecordAsync(database, ClearedEventType,
                "Dispatch hold cleared by " + who + ". It had been engaged for: " + cleared.Reason,
                new { cleared.Reason, cleared.SetBy, cleared.SetByUtc, ClearedBy = clearedBy }, token);
        }

        /// <summary>
        /// Record that startup restored a stored hold.
        /// </summary>
        /// <param name="database">Database driver whose Events collection receives the event.</param>
        /// <param name="restored">The restored hold.</param>
        /// <param name="token">Cancellation token.</param>
        public static Task RecordRestoredAsync(DatabaseDriver database, DispatchHoldSnapshot restored, CancellationToken token = default)
        {
            if (restored == null) throw new ArgumentNullException(nameof(restored));
            return RecordAsync(database, RestoredEventType, DispatchHold.RestoredMessage(restored),
                new { restored.Reason, restored.SetBy, restored.SetByUtc }, token);
        }

        /// <summary>
        /// Record that a vessel hold was engaged.
        /// </summary>
        /// <param name="database">Database driver whose Events collection receives the event.</param>
        /// <param name="hold">The engaged vessel hold.</param>
        /// <param name="token">Cancellation token.</param>
        public static Task RecordVesselEngagedAsync(DatabaseDriver database, DispatchHoldSnapshot hold, CancellationToken token = default)
        {
            if (hold == null) throw new ArgumentNullException(nameof(hold));
            string holder = String.IsNullOrWhiteSpace(hold.SetBy) ? "unknown" : hold.SetBy!;
            return RecordVesselAsync(database, VesselEngagedEventType, hold.VesselId,
                "Vessel " + hold.VesselId + " held by " + holder + ": " + hold.Reason,
                new { hold.VesselId, hold.Reason, hold.SetBy, hold.SetByUtc }, token);
        }

        /// <summary>
        /// Record that a vessel hold was cleared.
        /// </summary>
        /// <param name="database">Database driver whose Events collection receives the event.</param>
        /// <param name="cleared">The vessel hold that was cleared.</param>
        /// <param name="clearedBy">Identity of the caller that cleared the hold, or null.</param>
        /// <param name="token">Cancellation token.</param>
        public static Task RecordVesselClearedAsync(DatabaseDriver database, DispatchHoldSnapshot cleared, string? clearedBy, CancellationToken token = default)
        {
            if (cleared == null) throw new ArgumentNullException(nameof(cleared));
            string who = String.IsNullOrWhiteSpace(clearedBy) ? "unknown" : clearedBy!;
            return RecordVesselAsync(database, VesselClearedEventType, cleared.VesselId,
                "Vessel " + cleared.VesselId + " hold cleared by " + who + ". It had been held for: " + cleared.Reason,
                new { cleared.VesselId, cleared.Reason, cleared.SetBy, cleared.SetByUtc, ClearedBy = clearedBy }, token);
        }

        #endregion

        #region Private-Methods

        private static Task RecordVesselAsync(DatabaseDriver database, string eventType, string? vesselId, string message, object payload, CancellationToken token)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            ArmadaEvent evt = new ArmadaEvent(eventType, message)
            {
                EntityType = "vessel",
                EntityId = vesselId,
                VesselId = vesselId,
                Payload = JsonSerializer.Serialize(payload)
            };
            return database.Events.CreateAsync(evt, token);
        }

        private static Task RecordAsync(DatabaseDriver database, string eventType, string message, object payload, CancellationToken token)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            ArmadaEvent evt = new ArmadaEvent(eventType, message)
            {
                EntityType = "system",
                Payload = JsonSerializer.Serialize(payload)
            };
            return database.Events.CreateAsync(evt, token);
        }

        #endregion
    }
}
