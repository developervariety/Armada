namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Models;
    using Armada.Core.Services;
    using Armada.Core.Services.Interfaces;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// The operator operations on missions and voyages that REST, WebSocket and MCP all expose. Each operation owns its
    /// rule, its state change, its event and its broadcast; a surface reads the record under its caller's scope, calls
    /// the operation, and only turns the result into its own response shape.
    /// </summary>
    public sealed class MissionOperations
    {
        #region Public-Members

        /// <summary>
        /// Where the operations report what they changed.
        /// </summary>
        public OperationNotifier Notifier { get; }

        #endregion

        #region Private-Members

        private const string _Header = "[MissionOperations] ";

        private readonly DatabaseDriver _Database;
        private readonly ArmadaSettings _Settings;
        private readonly IDockService? _Docks;
        private readonly Func<string, CancellationToken, Task>? _RecallCaptain;
        private readonly LoggingModule? _Logging;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Instantiate.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="settings">Server settings (log directory, capacity limits).</param>
        /// <param name="docks">Dock service that removes a dock record and its worktree under its ownership guard.</param>
        /// <param name="recallCaptain">Captain recall, which stops the captain's agent process and releases it.</param>
        /// <param name="notifier">Event and broadcast sink.</param>
        /// <param name="logging">Optional logging module.</param>
        public MissionOperations(
            DatabaseDriver database,
            ArmadaSettings settings,
            IDockService? docks,
            Func<string, CancellationToken, Task>? recallCaptain,
            OperationNotifier? notifier,
            LoggingModule? logging = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _Docks = docks;
            _RecallCaptain = recallCaptain;
            Notifier = notifier ?? OperationNotifier.None;
            _Logging = logging;
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Cancel one mission through <see cref="MissionCancellation"/>, then write a <c>mission.cancelled</c> event
        /// and broadcast the change, and do the same with <c>mission.cancelled_dependency</c> for each waiting stage
        /// cancelled with it.
        /// </summary>
        /// <param name="mission">Mission, already read under the caller's scope.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>What changed, or the refusal.</returns>
        public async Task<MissionCancellationResult> CancelMissionAsync(Mission mission, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            MissionCancellationResult result = await MissionCancellation.CancelAsync(
                _Database, mission, MissionCancellation.OperatorCancelReason, _RecallCaptain, token).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _Logging?.Info(_Header + "cancel of mission " + mission.Id + " refused (" + result.Code + "): " + result.Message);
                return result;
            }

            Mission cancelled = result.Mission;
            await Notifier.EmitAsync("mission.cancelled", "Mission " + cancelled.Id + " cancelled by operator",
                "mission", cancelled.Id, result.RecalledCaptainId, cancelled.Id, cancelled.VesselId, cancelled.VoyageId).ConfigureAwait(false);
            Notifier.MissionChanged(cancelled);

            foreach (Mission dependent in result.CancelledDependents)
            {
                await Notifier.EmitAsync("mission.cancelled_dependency",
                    "Mission cancelled: blocked by cancelled dependency " + cancelled.Id,
                    "mission", dependent.Id, null, dependent.Id, dependent.VesselId, dependent.VoyageId).ConfigureAwait(false);
                Notifier.MissionChanged(dependent);
            }

            return result;
        }

        #endregion
    }
}
