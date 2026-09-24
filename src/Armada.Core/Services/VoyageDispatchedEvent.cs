namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Models;

    /// <summary>
    /// Records the <c>voyage.dispatched</c> event once a voyage exists: a dispatch with all of its
    /// missions, an autonomous rescue with its revise, retest and rejudge missions, or a voyage created
    /// without missions (the count is then zero and missions are added later). Every path that creates a
    /// voyage calls it after its last write, so a create that fails and is rolled back never announces a
    /// voyage.
    /// </summary>
    public static class VoyageDispatchedEvent
    {
        #region Public-Members

        /// <summary>
        /// Event type recorded for a dispatched voyage.
        /// </summary>
        public const string EventType = "voyage.dispatched";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record the event. A failure is logged and never fails the dispatch, because the voyage
        /// and its missions already exist.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="logging">Logging module; null when the caller logs nothing.</param>
        /// <param name="voyage">The dispatched voyage.</param>
        /// <param name="vesselId">Vessel the voyage targets.</param>
        /// <param name="missionCount">Number of missions the dispatch created.</param>
        /// <returns>Task.</returns>
        public static async Task EmitAsync(DatabaseDriver database, LoggingModule? logging, Voyage voyage, string? vesselId, int missionCount)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (voyage == null) throw new ArgumentNullException(nameof(voyage));

            try
            {
                ArmadaEvent evt = new ArmadaEvent(EventType,
                    "Voyage dispatched: " + voyage.Title + " (" + missionCount + " mission" + (missionCount == 1 ? "" : "s") + ")");
                evt.TenantId = voyage.TenantId;
                evt.UserId = voyage.UserId;
                evt.EntityType = "voyage";
                evt.EntityId = voyage.Id;
                evt.VoyageId = voyage.Id;
                evt.VesselId = vesselId;
                // The dispatch already committed, so the record is written even if the caller's request ends.
                await database.Events.CreateAsync(evt, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logging?.Warn("[VoyageDispatchedEvent] could not record " + EventType + " for voyage " + voyage.Id + ": " + ex.Message);
            }
        }

        #endregion
    }
}
