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
    /// <para>
    /// Cleanup is best-effort: the parent row is already deleted, so a dependent that cannot be removed must not
    /// fail the delete. Each such dependent is skipped with a named reason, logged as a warning when a logging
    /// module is supplied, and counted in the returned <see cref="CascadeCleanupResult"/>.
    /// </para>
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
        /// <param name="logging">Optional logging module; each skipped event is logged as a warning.</param>
        /// <returns>The events removed and each event skipped with its reason.</returns>
        public static async Task<CascadeCleanupResult> RemoveEventsForVesselAsync(DatabaseDriver database, string vesselId, CancellationToken token = default, LoggingModule? logging = null)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            CascadeCleanupResult result = new CascadeCleanupResult();
            if (String.IsNullOrEmpty(vesselId)) return result;
            await _DeleteEventsAsync(database, (int limit) => database.Events.EnumerateByVesselAsync(vesselId, limit, token), result, logging, token).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Remove all telemetry events that reference the supplied mission.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="missionId">Mission identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="logging">Optional logging module; each skipped event is logged as a warning.</param>
        /// <returns>The events removed and each event skipped with its reason.</returns>
        public static async Task<CascadeCleanupResult> RemoveEventsForMissionAsync(DatabaseDriver database, string missionId, CancellationToken token = default, LoggingModule? logging = null)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            CascadeCleanupResult result = new CascadeCleanupResult();
            if (String.IsNullOrEmpty(missionId)) return result;
            await _DeleteEventsAsync(database, (int limit) => database.Events.EnumerateByMissionAsync(missionId, limit, token), result, logging, token).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Remove all telemetry events that reference the supplied voyage.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="voyageId">Voyage identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="logging">Optional logging module; each skipped event is logged as a warning.</param>
        /// <returns>The events removed and each event skipped with its reason.</returns>
        public static async Task<CascadeCleanupResult> RemoveEventsForVoyageAsync(DatabaseDriver database, string voyageId, CancellationToken token = default, LoggingModule? logging = null)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            CascadeCleanupResult result = new CascadeCleanupResult();
            if (String.IsNullOrEmpty(voyageId)) return result;
            await _DeleteEventsAsync(database, (int limit) => database.Events.EnumerateByVoyageAsync(voyageId, limit, token), result, logging, token).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Remove dependents that reference the supplied captain: telemetry events, planning sessions and
        /// objective refinement sessions. Both session tables store a non-nullable captain id without a foreign
        /// key, so they must be removed explicitly to avoid dangling sessions that reference a deleted captain.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="captainId">Captain identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="logging">Optional logging module; each skipped dependent is logged as a warning.</param>
        /// <returns>The dependent rows removed and each row or list skipped with its reason.</returns>
        public static async Task<CascadeCleanupResult> RemoveDependentsForCaptainAsync(DatabaseDriver database, string captainId, CancellationToken token = default, LoggingModule? logging = null)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            CascadeCleanupResult result = new CascadeCleanupResult();
            if (String.IsNullOrEmpty(captainId)) return result;

            try
            {
                await _DeleteEventsAsync(database, (int limit) => database.Events.EnumerateByCaptainAsync(captainId, limit, token), result, logging, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // An events-deletion failure leaves orphan telemetry, not a blocked delete.
                _Skip(result, logging, "EventList", captainId, "events of deleted captain " + captainId + " were not removed: " + ex.Message);
            }

            List<PlanningSession> sessions = new List<PlanningSession>();
            try
            {
                sessions = await database.PlanningSessions.EnumerateByCaptainAsync(captainId, token).ConfigureAwait(false);
            }
            catch (NotSupportedException ex)
            {
                // The provider stores no planning sessions, so there are none to remove and nothing is skipped.
                logging?.Info(_Header + "planning-session cleanup skipped for deleted captain " + captainId + ": " + ex.Message);
            }
            catch (Exception ex)
            {
                // An unreadable planning-session list leaves orphans for a later sweep.
                _Skip(result, logging, "PlanningSessionList", captainId, "planning sessions of deleted captain " + captainId + " were not listed and are left in place: " + ex.Message);
            }
            foreach (PlanningSession session in sessions)
            {
                try
                {
                    await database.PlanningSessions.DeleteAsync(session.Id, token).ConfigureAwait(false);
                    result.Removed++;
                }
                catch (Exception ex)
                {
                    // A session that cannot be removed is skipped so one failure does not block the rest
                    // of the cascade. The parent delete still proceeds.
                    _Skip(result, logging, "PlanningSession", session.Id, "planning session " + session.Id + " of deleted captain " + captainId + " was not removed: " + ex.Message);
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
                _Skip(result, logging, "RefinementSessionList", captainId, "refinement sessions of deleted captain " + captainId + " were not listed and are left in place: " + ex.Message);
            }
            foreach (ObjectiveRefinementSession session in refinementSessions)
            {
                try
                {
                    await database.ObjectiveRefinementSessions.DeleteAsync(session.Id, token).ConfigureAwait(false);
                    result.Removed++;
                }
                catch (Exception ex)
                {
                    // Skipped as for planning sessions above.
                    _Skip(result, logging, "RefinementSession", session.Id, "refinement session " + session.Id + " of deleted captain " + captainId + " was not removed: " + ex.Message);
                }
            }

            return result;
        }

        #endregion

        #region Private-Methods

        private static async Task _DeleteEventsAsync(DatabaseDriver database, Func<int, Task<List<ArmadaEvent>>> fetch, CascadeCleanupResult result, LoggingModule? logging, CancellationToken token)
        {
            HashSet<string> skipped = new HashSet<string>(StringComparer.Ordinal);

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
                        result.Removed++;
                        removedThisPass++;
                    }
                    catch (Exception ex)
                    {
                        // A row that fails again on a later pass is reported once.
                        if (skipped.Add(evt.Id))
                            _Skip(result, logging, "Event", evt.Id, "event " + evt.Id + " was not removed: " + ex.Message);
                    }
                }

                // If nothing in this pass could be deleted, stop to avoid re-fetching the same rows forever.
                if (removedThisPass == 0) break;
            }
        }

        private static void _Skip(CascadeCleanupResult result, LoggingModule? logging, string kind, string id, string reason)
        {
            result.Skips.Add(new CascadeCleanupSkip(kind, id, reason));
            logging?.Warn(_Header + reason);
        }

        #endregion
    }
}
