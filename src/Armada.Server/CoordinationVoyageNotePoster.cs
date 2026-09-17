namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Posts a voyage-tagged coordination-board note through <see cref="CoordinationService"/>. The
    /// note is authored by the system and addressed to no participant; the voyage identifier on the
    /// post is what carries it into that voyage's next stage brief. The poster is best-effort and
    /// never throws into the caller: it changes no other Armada record and carries no authority.
    /// </summary>
    public sealed class CoordinationVoyageNotePoster : IVoyageTaggedNotePoster
    {
        #region Private-Members

        private const string _Header = "[CoordinationVoyageNotePoster] ";

        private readonly CoordinationService _Coordination;
        private readonly LoggingModule _Logging;
        private readonly string _AuthorName;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create the voyage-tagged note poster.
        /// </summary>
        /// <param name="coordination">Coordination service used to post the note.</param>
        /// <param name="authorName">Display name recorded as the note's author.</param>
        /// <param name="logging">Logging module.</param>
        public CoordinationVoyageNotePoster(CoordinationService coordination, string authorName, LoggingModule logging)
        {
            _Coordination = coordination ?? throw new ArgumentNullException(nameof(coordination));
            if (String.IsNullOrWhiteSpace(authorName)) throw new ArgumentException("Author name must not be empty.", nameof(authorName));
            _AuthorName = authorName;
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task PostVoyageNoteAsync(string content, string? voyageId, string? missionId, string? vesselId, CancellationToken token)
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
                    voyageId: voyageId,
                    missionId: missionId,
                    vesselId: vesselId,
                    incidentId: null,
                    tenantId: null,
                    toParticipantKey: null,
                    token: token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to post voyage-tagged note: " + ex.Message);
            }
        }

        #endregion
    }
}
