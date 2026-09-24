namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using SyslogLogging;

    /// <summary>
    /// Mirrors selected fleet events onto the coordination board as system notes, so concurrent
    /// operator sessions see fleet activity in the chatroom. It observes the event store itself, so
    /// every producer of a mirrored event type reaches the board, whichever service wrote the event.
    /// <see cref="CoordinationService.BuildSystemNoteContent"/> decides which types are mirrored.
    /// </summary>
    public sealed class CoordinationFleetEventMirror
    {
        #region Private-Members

        private const string _Header = "[CoordinationFleetEventMirror] ";
        private const string _AuthorName = "armada";

        private readonly CoordinationService _Coordination;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create the mirror.
        /// </summary>
        /// <param name="coordination">Coordination service that posts the note.</param>
        public CoordinationFleetEventMirror(CoordinationService coordination)
        {
            _Coordination = coordination ?? throw new ArgumentNullException(nameof(coordination));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Mirror every event the driver stores from now on. A note that cannot be posted is logged
        /// with the event type and never fails the event write.
        /// </summary>
        /// <param name="database">Database driver whose event writes are observed.</param>
        /// <param name="coordination">Coordination service that posts the note.</param>
        /// <param name="logging">Logging module.</param>
        /// <returns>The attached mirror.</returns>
        public static CoordinationFleetEventMirror Attach(DatabaseDriver database, CoordinationService coordination, LoggingModule logging)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (logging == null) throw new ArgumentNullException(nameof(logging));
            CoordinationFleetEventMirror mirror = new CoordinationFleetEventMirror(coordination);
            database.AddEventCreatedObserver(
                mirror.MirrorAsync,
                (evt, ex) => logging.Warn(_Header + "failed to mirror event " + evt.EventType + " to the coordination board: " + ex.Message));
            return mirror;
        }

        /// <summary>
        /// Post the board note for one stored event, when its type is mirrored.
        /// </summary>
        /// <param name="evt">The stored event.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Task.</returns>
        public async Task MirrorAsync(ArmadaEvent evt, CancellationToken token = default)
        {
            if (evt == null) return;
            string? note = CoordinationService.BuildSystemNoteContent(
                evt.EventType, evt.Message, evt.EntityType, evt.EntityId, evt.VoyageId, evt.MissionId, evt.VesselId);
            if (note == null) return;

            await _Coordination.PostMessageAsync(
                CoordinationService.DefaultRoomKey,
                CoordinationAuthorTypeEnum.System,
                null,
                _AuthorName,
                note,
                evt.VoyageId,
                evt.MissionId,
                evt.VesselId,
                null,
                null,
                token: token).ConfigureAwait(false);
        }

        #endregion
    }
}
