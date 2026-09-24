namespace Armada.Core.Services
{
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;
    using Armada.Core.Settings;
    using SyslogLogging;

    /// <summary>
    /// Restarts terminal missions through the same durable capacity gate as new dispatches.
    /// </summary>
    public sealed class MissionRestartService
    {
        private readonly DatabaseDriver _Database;
        private readonly FleetCapacityAdmission _Capacity;
        private readonly LoggingModule? _Logging;

        /// <summary>Instantiate.</summary>
        public MissionRestartService(DatabaseDriver database, ArmadaSettings settings, LoggingModule? logging = null)
        {
            _Database = database ?? throw new ArgumentNullException(nameof(database));
            _Logging = logging;
            _Capacity = new FleetCapacityAdmission(database, settings ?? throw new ArgumentNullException(nameof(settings)), logging);
        }

        /// <summary>
        /// Refusal code for a LandingFailed mission, whose produced work is landed with retry-landing instead.
        /// </summary>
        public const string UseRetryLandingCode = "use_retry_landing";

        /// <summary>
        /// Refusal code for a mission in any other status that cannot be restarted.
        /// </summary>
        public const string NotRestartableCode = "mission_not_restartable";

        /// <summary>
        /// The one restart eligibility rule: only a Failed or Cancelled mission is restarted. A LandingFailed mission
        /// holds produced work that a restart would discard, so it is refused with a pointer to retry-landing.
        /// </summary>
        /// <param name="mission">Mission to restart.</param>
        /// <param name="code">Refusal code, or null when the mission may be restarted.</param>
        /// <returns>The refusal reason, or null when the mission may be restarted.</returns>
        public static string? FindIneligibility(Mission mission, out string? code)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            code = null;
            if (mission.Status == MissionStatusEnum.Failed || mission.Status == MissionStatusEnum.Cancelled) return null;
            if (mission.Status == MissionStatusEnum.LandingFailed)
            {
                code = UseRetryLandingCode;
                return "Mission " + mission.Id + " is LandingFailed and keeps its produced work; land it with retry-landing "
                    + "(POST /api/v1/missions/" + mission.Id + "/retry-landing, or armada_retry_landing) instead of restarting it.";
            }
            code = NotRestartableCode;
            return "Only Failed or Cancelled missions can be restarted (current: " + mission.Status + ").";
        }

        /// <summary>Reset a Failed or Cancelled mission to Pending after reserving its work unit.</summary>
        public async Task<Mission> RestartAsync(
            Mission mission,
            string? title = null,
            string? description = null,
            CancellationToken token = default)
        {
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            string? ineligible = FindIneligibility(mission, out _);
            if (ineligible != null) throw new InvalidOperationException(ineligible);
            if (String.IsNullOrWhiteSpace(mission.VesselId))
                throw new InvalidOperationException("Mission does not have an associated vessel.");

            Vessel? vessel = await _Database.Vessels.ReadAsync(mission.VesselId, token).ConfigureAwait(false);
            if (vessel == null) throw new InvalidOperationException("Vessel not found: " + mission.VesselId);

            await using FleetCapacityReservation admission = await _Capacity
                .AcquireAsync(vessel, mission.VoyageId, token).ConfigureAwait(false);

            if (!String.IsNullOrWhiteSpace(title)) mission.Title = title;
            if (!String.IsNullOrWhiteSpace(description)) mission.Description = description;
            mission.Status = MissionStatusEnum.Pending;
            mission.CreatedUtc = DateTime.UtcNow;
            mission.CaptainId = null;
            mission.BranchName = null;
            mission.PrUrl = null;
            mission.CommitHash = null;
            mission.DockId = null;
            mission.ProcessId = null;
            mission.DiffSnapshot = null;
            mission.FailureReason = null;
            TerminalVoyageMissionRule.ClearReconciledOutcome(mission);
            mission.StartedUtc = null;
            mission.CompletedUtc = null;
            mission.LastUpdateUtc = DateTime.UtcNow;
            mission = await _Database.Missions.UpdateAsync(mission, token).ConfigureAwait(false);
            try
            {
                await admission.VerifyOwnershipAsync(token).ConfigureAwait(false);
            }
            catch
            {
                mission.Status = MissionStatusEnum.Cancelled;
                mission.FailureReason = "Mission restart cancelled because fleet-capacity admission was lost.";
                mission.CompletedUtc = DateTime.UtcNow;
                mission.LastUpdateUtc = DateTime.UtcNow;
                await _Database.Missions.UpdateAsync(mission, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            await MissionAttemptFactRecorder.RecordAsync(_Database, mission, MissionAttemptFactTypeEnum.Restarted, "restarted", _Logging, token).ConfigureAwait(false);
            return mission;
        }
    }
}
