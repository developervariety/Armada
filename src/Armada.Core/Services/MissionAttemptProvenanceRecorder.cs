namespace Armada.Core.Services
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using SyslogLogging;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The single rule that writes a per-attempt provenance row when a captain process launches and closes it when
    /// the process exit is observed. A recording failure is logged with its reason and never fails the launch or the
    /// exit handling that called it.
    /// </summary>
    public static class MissionAttemptProvenanceRecorder
    {
        #region Public-Members

        /// <summary>Reason code of an attempt row closed because a newer launch replaced it.</summary>
        public const string SupersededReason = "superseded_by_relaunch";

        /// <summary>Reason code of an attempt whose exit carried no usable exit code.</summary>
        public const string ExitUnknownReason = "exit_unknown";

        /// <summary>Reason code of an attempt that exited with a non-zero code.</summary>
        public const string ExitNonZeroReason = "exit_nonzero";

        #endregion

        #region Public-Methods

        /// <summary>
        /// Record the start of one attempt. Rows still running for the mission belong to earlier launches that
        /// never reported an exit, so they close as interrupted first.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="mission">Mission the attempt works.</param>
        /// <param name="captain">Captain that runs it.</param>
        /// <param name="processId">Process id of the launch.</param>
        /// <param name="runtimeVersion">Version of the runtime executable, or null when unknown.</param>
        /// <param name="logging">Optional logger.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The stored row, or null when it could not be stored.</returns>
        public static async Task<MissionAttemptProvenance?> StartAsync(
            DatabaseDriver database,
            Mission mission,
            Captain captain,
            int processId,
            string? runtimeVersion,
            LoggingModule? logging = null,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (mission == null) throw new ArgumentNullException(nameof(mission));
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            try
            {
                DateTime now = DateTime.UtcNow;
                await database.MissionAttempts.InterruptRunningAsync(mission.Id, SupersededReason, now, token).ConfigureAwait(false);
                MissionAttemptProvenance record = new MissionAttemptProvenance
                {
                    TenantId = mission.TenantId,
                    UserId = mission.UserId,
                    MissionId = mission.Id,
                    VoyageId = mission.VoyageId,
                    VesselId = mission.VesselId,
                    CaptainId = captain.Id,
                    Persona = mission.Persona,
                    Runtime = captain.Runtime.ToString(),
                    Model = captain.Model,
                    RuntimeVersion = runtimeVersion,
                    ProcessId = processId,
                    StartedUtc = now,
                    Outcome = MissionAttemptOutcomeEnum.Running
                };
                return await database.MissionAttempts.CreateAsync(record, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logging?.Warn("[MissionAttemptProvenance] could not record the start of mission " + mission.Id + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Close the running row of one process when its exit is observed.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="missionId">Mission the process worked.</param>
        /// <param name="processId">Exiting process id.</param>
        /// <param name="exitCode">Exit code, or null when none was observed.</param>
        /// <param name="logging">Optional logger.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True when a running row was closed.</returns>
        public static async Task<bool> CloseAsync(
            DatabaseDriver database,
            string missionId,
            int processId,
            int? exitCode,
            LoggingModule? logging = null,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (String.IsNullOrWhiteSpace(missionId)) return false;
            try
            {
                MissionAttemptOutcomeEnum outcome = OutcomeOf(exitCode, out string? reason);
                int closed = await database.MissionAttempts.CloseAsync(missionId, processId, outcome, exitCode, reason, DateTime.UtcNow, token).ConfigureAwait(false);
                return closed > 0;
            }
            catch (Exception ex)
            {
                logging?.Warn("[MissionAttemptProvenance] could not record the end of mission " + missionId + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Classify an observed exit code. Zero is completed; a missing code or the supervisor's kill sentinel is
        /// interrupted; anything else failed.
        /// </summary>
        /// <param name="exitCode">Exit code, or null.</param>
        /// <param name="reason">Reason code for the classification, or null for a clean exit.</param>
        /// <returns>The outcome.</returns>
        public static MissionAttemptOutcomeEnum OutcomeOf(int? exitCode, out string? reason)
        {
            if (!exitCode.HasValue || exitCode.Value == -1)
            {
                reason = ExitUnknownReason;
                return MissionAttemptOutcomeEnum.Interrupted;
            }
            if (exitCode.Value == 0)
            {
                reason = null;
                return MissionAttemptOutcomeEnum.Completed;
            }
            reason = ExitNonZeroReason;
            return MissionAttemptOutcomeEnum.Failed;
        }

        #endregion
    }
}
