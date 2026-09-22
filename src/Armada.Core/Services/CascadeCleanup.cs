namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Centralized cascade cleanup for dependent rows the database schema does not remove on its own.
    /// The <c>events</c> and <c>planning_sessions</c> tables carry plain (non-foreign-key) references to
    /// their parent entities (captain, mission, vessel, voyage), so when a parent is hard-deleted those
    /// rows would otherwise dangle and later fail to resolve -- for example an event whose linked entity
    /// is gone renders a "could not be loaded" error when opened. Every hard-delete path routes through
    /// this class so the rules live in exactly one place and stay consistent across entities.
    /// </summary>
    public static class CascadeCleanup
    {
        #region Private-Members

        private const int _BatchSize = 500;
        private const string _Header = "[CascadeCleanup] ";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Remove all telemetry events that reference the supplied vessel.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="vesselId">Vessel identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of events removed.</returns>
        public static Task<int> RemoveEventsForVesselAsync(DatabaseDriver database, string vesselId, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrEmpty(vesselId)) return Task.FromResult(0);
            return _DeleteEventsAsync(database, (int limit) => database.Events.EnumerateByVesselAsync(vesselId, limit, token), token);
        }

        /// <summary>
        /// Remove all telemetry events that reference the supplied mission.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of events removed.</returns>
        public static Task<int> RemoveEventsForMissionAsync(DatabaseDriver database, string missionId, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrEmpty(missionId)) return Task.FromResult(0);
            return _DeleteEventsAsync(database, (int limit) => database.Events.EnumerateByMissionAsync(missionId, limit, token), token);
        }

        /// <summary>
        /// Remove all telemetry events that reference the supplied voyage.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="voyageId">Voyage identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of events removed.</returns>
        public static Task<int> RemoveEventsForVoyageAsync(DatabaseDriver database, string voyageId, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrEmpty(voyageId)) return Task.FromResult(0);
            return _DeleteEventsAsync(database, (int limit) => database.Events.EnumerateByVoyageAsync(voyageId, limit, token), token);
        }

        /// <summary>
        /// Remove dependents that reference the supplied captain: telemetry events, planning sessions and
        /// objective refinement sessions. Both session tables store a non-nullable captain id without a foreign
        /// key, so they must be removed explicitly to avoid dangling sessions that reference a deleted captain.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="logging">Logging module; each dependent that is not removed is logged here with its reason.</param>
        /// <returns>The number of dependent rows removed.</returns>
        public static async Task<int> RemoveDependentsForCaptainAsync(DatabaseDriver database, string captainId, CancellationToken token = default, LoggingModule? logging = null)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrEmpty(captainId)) return 0;

            // Best-effort throughout: the parent captain row is already deleted by the caller, so a
            // dependent-cleanup failure must not surface as a failed delete. Each step is guarded and
            // a failure leaves an orphan for a later sweep rather than throwing.
            int removed = 0;
            try
            {
                removed = await _DeleteEventsAsync(database, (int limit) => database.Events.EnumerateByCaptainAsync(captainId, limit, token), token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // An events-deletion failure leaves orphan telemetry, not a blocked delete.
                logging?.Warn(_Header + "events of deleted captain " + captainId + " were not removed: " + ex.Message);
            }

            List<PlanningSession> sessions = new List<PlanningSession>();
            try
            {
                sessions = await database.PlanningSessions.EnumerateByCaptainAsync(captainId, token).ConfigureAwait(false);
            }
            catch (NotSupportedException ex)
            {
                // The provider stores no planning sessions, so there are none to remove.
                logging?.Info(_Header + "planning-session cleanup skipped for deleted captain " + captainId + ": " + ex.Message);
            }
            catch (Exception ex)
            {
                // An unreadable planning-session list leaves orphans for a later sweep.
                logging?.Warn(_Header + "planning sessions of deleted captain " + captainId + " were not listed and are left in place: " + ex.Message);
            }
            foreach (PlanningSession session in sessions)
            {
                try
                {
                    await database.PlanningSessions.DeleteAsync(session.Id, token).ConfigureAwait(false);
                    removed++;
                }
                catch (Exception ex)
                {
                    // A session that cannot be removed is skipped so one failure does not block the rest
                    // of the cascade. The parent delete still proceeds.
                    logging?.Warn(_Header + "planning session " + session.Id + " of deleted captain " + captainId + " was not removed: " + ex.Message);
                }
            }

            List<ObjectiveRefinementSession> refinementSessions = new List<ObjectiveRefinementSession>();
            try
            {
                refinementSessions = await database.ObjectiveRefinementSessions.EnumerateByCaptainAsync(captainId, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // An unreadable refinement-session list leaves orphans for a later sweep.
                logging?.Warn(_Header + "refinement sessions of deleted captain " + captainId + " were not listed and are left in place: " + ex.Message);
            }
            foreach (ObjectiveRefinementSession session in refinementSessions)
            {
                try
                {
                    await database.ObjectiveRefinementSessions.DeleteAsync(session.Id, token).ConfigureAwait(false);
                    removed++;
                }
                catch (Exception ex)
                {
                    // Skipped as for planning sessions above.
                    logging?.Warn(_Header + "refinement session " + session.Id + " of deleted captain " + captainId + " was not removed: " + ex.Message);
                }
            }

            return removed;
        }

        #endregion

        #region Private-Methods

        private static async Task<int> _DeleteEventsAsync(DatabaseDriver database, Func<int, Task<List<ArmadaEvent>>> fetch, CancellationToken token)
        {
            int removed = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();

                List<ArmadaEvent> batch = await fetch(_BatchSize).ConfigureAwait(false);
                if (batch == null || batch.Count == 0) break;

                int removedThisPass = 0;
                foreach (ArmadaEvent evt in batch)
                {
                    try
                    {
                        await database.Events.DeleteAsync(evt.Id, token).ConfigureAwait(false);
                        removed++;
                        removedThisPass++;
                    }
                    catch (Exception)
                    {
                        // Best-effort per row; continue with the rest of the batch.
                    }
                }

                // If nothing in this pass could be deleted, stop to avoid re-fetching the same rows forever.
                if (removedThisPass == 0) break;
            }

            return removed;
        }

        #endregion
    }
}
