namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Posts a broadcast coordination-board note through <see cref="CoordinationService"/> for a typed
    /// decision that surfaced a condition every operator session should see. The note is authored by
    /// the system and addressed to no participant. The poster is best-effort and never throws into the
    /// caller: it changes no other Armada record and carries no authority.
    /// </summary>
    public sealed class CoordinationBroadcastNotePoster : IBoardNotePoster
    {
        #region Private-Members

        private const string _Header = "[CoordinationBroadcastNotePoster] ";
        private const string _AuthorName = "Typed Decision";

        private readonly CoordinationService _Coordination;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create the broadcast note poster.
        /// </summary>
        /// <param name="coordination">Coordination service used to post the note.</param>
        /// <param name="logging">Logging module.</param>
        public CoordinationBroadcastNotePoster(CoordinationService coordination, LoggingModule logging)
        {
            _Coordination = coordination ?? throw new ArgumentNullException(nameof(coordination));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task PostBroadcastAsync(string content, string? vesselId, string? missionId, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(content)) return;
            try
            {
                await _Coordination.PostMessageAsync(
                    CoordinationService.DefaultRoomKey,
                    CoordinationAuthorTypeEnum.System,
                    authorId: null,
                    authorName: _AuthorName,
                    content: content,
                    voyageId: null,
                    missionId: missionId,
                    vesselId: vesselId,
                    incidentId: null,
                    tenantId: null,
                    toParticipantKey: null,
                    token: token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to post broadcast note: " + ex.Message);
            }
        }

        #endregion
    }
}
