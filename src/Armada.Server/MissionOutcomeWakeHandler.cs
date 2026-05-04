namespace Armada.Server
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Services.Interfaces;
    using SyslogLogging;

    /// <summary>
    /// Wires MissionService.OnMissionOutcome to IRemoteTriggerService.FireDrainerAsync,
    /// covering the mission outcomes that bypass <see cref="MissionLandingHandler"/>.
    /// MissionLandingHandler already fires drainer wake-ups for protected-paths
    /// MissionFailed, merge-queue WorkProduced, and merge-queue auto_land_skipped, so this
    /// handler only fires when MissionService reports that the landing handler will NOT
    /// run for the mission. Heartbeat events do not flow through OnMissionOutcome and are
    /// therefore never woken from here.
    /// </summary>
    public sealed class MissionOutcomeWakeHandler
    {
        #region Private-Members

        private readonly IRemoteTriggerService _RemoteTrigger;
        private readonly LoggingModule _Logging;
        private const string _Header = "[MissionOutcomeWakeHandler] ";

        #endregion

        #region Constructors-and-Factories

        /// <summary>Constructs a new MissionOutcomeWakeHandler.</summary>
        /// <param name="remoteTrigger">Remote trigger service that performs the wake-up.</param>
        /// <param name="logging">Logging module.</param>
        public MissionOutcomeWakeHandler(IRemoteTriggerService remoteTrigger, LoggingModule logging)
        {
            _RemoteTrigger = remoteTrigger ?? throw new ArgumentNullException(nameof(remoteTrigger));
            _Logging = logging ?? throw new ArgumentNullException(nameof(logging));
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Handle a mission outcome notification by firing a remote-trigger drainer when appropriate.
        /// </summary>
        /// <param name="mission">Mission whose outcome was just emitted.</param>
        /// <param name="willInvokeLandingHandler">True when MissionLandingHandler will subsequently
        /// run for this mission. The landing handler fires its own drainer wake-ups in the qualifying
        /// branches; this handler stays silent in that case to avoid duplicate firings.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task HandleAsync(Mission mission, bool willInvokeLandingHandler, CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            if (willInvokeLandingHandler)
            {
                return;
            }

            string? text = BuildWakeText(mission);
            if (text == null)
            {
                return;
            }

            string vesselId = mission.VesselId ?? string.Empty;

            try
            {
                await _RemoteTrigger.FireDrainerAsync(vesselId, text, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _Logging.Warn(_Header + "FireDrainerAsync failed for mission " + mission.Id + " outcome " + mission.Status + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Builds the wake-up text for the given mission outcome, or returns null when
        /// the status does not warrant a wake-up. Exposed for direct routing tests.
        /// </summary>
        /// <param name="mission">Mission whose outcome should be described.</param>
        /// <returns>Wake-up text, or null when the status does not warrant a wake-up.</returns>
        public static string? BuildWakeText(Mission mission)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));

            string vesselId = mission.VesselId ?? string.Empty;
            string title = mission.Title ?? string.Empty;

            switch (mission.Status)
            {
                case MissionStatusEnum.WorkProduced:
                case MissionStatusEnum.PullRequestOpen:
                    return "WorkProduced: mission " + mission.Id + " (" + title + ") on vessel " + vesselId;
                case MissionStatusEnum.Failed:
                    return "MissionFailed: mission " + mission.Id + " (" + title + ") :: " + (mission.FailureReason ?? "Failed");
                case MissionStatusEnum.LandingFailed:
                    return "MissionFailed: mission " + mission.Id + " (" + title + ") :: " + (mission.FailureReason ?? "LandingFailed");
                case MissionStatusEnum.Cancelled:
                    return "MissionFailed: mission " + mission.Id + " (" + title + ") :: " + (mission.FailureReason ?? "Cancelled");
                default:
                    return null;
            }
        }

        #endregion
    }
}
