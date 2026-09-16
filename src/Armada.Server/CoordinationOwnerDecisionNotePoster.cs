namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Posts an owner-addressed coordination-board note through <see cref="CoordinationService"/> for a
    /// typed decision that surfaced a question only the owner can answer. The note is authored by the
    /// system and addressed to the registered AgentWake session's participant key when one is known, so
    /// the addressed-note wake reaches the owner; with no registered session the note is a broadcast the
    /// owner still sees on the next board read. The poster is best-effort and never throws into the
    /// caller: it changes no other Armada record and carries no authority.
    /// </summary>
    public sealed class CoordinationOwnerDecisionNotePoster : IOwnerDecisionNotePoster
    {
        #region Private-Members

        private const string _Header = "[CoordinationOwnerDecisionNotePoster] ";
        private const string _AuthorName = "Typed Decision";

        private readonly CoordinationService _Coordination;
        private readonly Func<string?> _ResolveOwnerParticipantKey;
        private readonly LoggingModule _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Create the owner-decision note poster.
        /// </summary>
        /// <param name="coordination">Coordination service used to post the note.</param>
        /// <param name="resolveOwnerParticipantKey">Resolves the owner participant key at post time; may return null.</param>
        /// <param name="logging">Logging module.</param>
        public CoordinationOwnerDecisionNotePoster(
            CoordinationService coordination,
            Func<string?> resolveOwnerParticipantKey,
            LoggingModule logging)
        {
            _Coordination = coordination ?? throw new ArgumentNullException(nameof(coordination));
            _ResolveOwnerParticipantKey = resolveOwnerParticipantKey ?? throw new ArgumentNullException(nameof(resolveOwnerParticipantKey));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <inheritdoc />
        public async Task PostOwnerDecisionAsync(string content, string? vesselId, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(content)) return;
            try
            {
                string? owner = _ResolveOwnerParticipantKey();
                await _Coordination.PostMessageAsync(
                    CoordinationService.DefaultRoomKey,
                    CoordinationAuthorTypeEnum.System,
                    authorId: null,
                    authorName: _AuthorName,
                    content: content,
                    voyageId: null,
                    missionId: null,
                    vesselId: vesselId,
                    incidentId: null,
                    tenantId: null,
                    toParticipantKey: owner,
                    token: token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "failed to post owner-decision note: " + ex.Message);
            }
        }

        #endregion
    }
}
