namespace Armada.Core.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Database;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// Whether a Challenger is still ruling on a failed Judge. Autonomous recovery waits on it before
    /// a rescue, and the objective scheduler waits on it before it dispatches the same objective again.
    /// </summary>
    public static class JudgeChallengePending
    {
        #region Public-Methods

        /// <summary>
        /// Return the id of the Challenger still ruling on this Judge failure, or null when none is.
        /// A Challenger dispatched longer ago than the timeout no longer holds the failure.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="failed">The failed Judge mission.</param>
        /// <param name="timeout">How long a dispatched Challenger holds the failure.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The pending Challenger mission id, or null.</returns>
        public static async Task<string?> FindPendingChallengerIdAsync(
            DatabaseDriver database,
            Mission failed,
            TimeSpan timeout,
            CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (failed == null) throw new ArgumentNullException(nameof(failed));

            ArmadaEvent? dispatched = await ReadDispatchAsync(database, failed, token).ConfigureAwait(false);
            if (dispatched == null) return null;
            JudgeChallengeDispatchRecord record = JudgeChallenge.ReadDispatchRecord(dispatched.Payload);
            if (String.IsNullOrWhiteSpace(record.ChallengerMissionId)) return null;

            Mission? challenger = await ReadMissionAsync(database, failed.TenantId, record.ChallengerMissionId, token).ConfigureAwait(false);
            if (challenger == null || IsChallengerTerminal(challenger.Status)) return null;
            return DateTime.UtcNow - dispatched.CreatedUtc < timeout ? challenger.Id : null;
        }

        /// <summary>
        /// Read the latest Challenger dispatch event recorded for this Judge failure.
        /// </summary>
        /// <param name="database">Database driver.</param>
        /// <param name="failed">The failed Judge mission.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The dispatch event, or null when no Challenger was dispatched for this failure.</returns>
        public static async Task<ArmadaEvent?> ReadDispatchAsync(DatabaseDriver database, Mission failed, CancellationToken token = default)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (failed == null) throw new ArgumentNullException(nameof(failed));

            List<ArmadaEvent> events = await database.Events.EnumerateByMissionAsync(failed.Id, 200, token).ConfigureAwait(false);
            return events
                .Where(evt => String.Equals(evt.EventType, JudgeChallenge.DispatchedEvent, StringComparison.Ordinal))
                .Where(evt => BelongsToFailure(JudgeChallenge.ReadDispatchRecord(evt.Payload), failed))
                .OrderByDescending(evt => evt.CreatedUtc)
                .FirstOrDefault();
        }

        /// <summary>
        /// True when the Challenger mission has ended.
        /// </summary>
        /// <param name="status">Challenger mission status.</param>
        /// <returns>True for a terminal status.</returns>
        public static bool IsChallengerTerminal(MissionStatusEnum status)
        {
            return status == MissionStatusEnum.Complete
                || status == MissionStatusEnum.Failed
                || status == MissionStatusEnum.LandingFailed
                || status == MissionStatusEnum.Cancelled;
        }

        #endregion

        #region Private-Methods

        // A challenge belongs to the failure it was dispatched for. A Judge restarted and failed again is a
        // new failure with a new completion time, and gets its own challenge.
        private static bool BelongsToFailure(JudgeChallengeDispatchRecord record, Mission failed)
        {
            if (!record.JudgeCompletedUtc.HasValue || !failed.CompletedUtc.HasValue)
                return record.JudgeCompletedUtc.HasValue == failed.CompletedUtc.HasValue;
            return Math.Abs((record.JudgeCompletedUtc.Value - failed.CompletedUtc.Value).TotalSeconds) < 1.0;
        }

        private static async Task<Mission?> ReadMissionAsync(DatabaseDriver database, string? tenantId, string missionId, CancellationToken token)
        {
            if (!String.IsNullOrWhiteSpace(tenantId))
            {
                Mission? tenantScoped = await database.Missions.ReadAsync(tenantId, missionId, token).ConfigureAwait(false);
                if (tenantScoped != null) return tenantScoped;
            }

            return await database.Missions.ReadAsync(missionId, token).ConfigureAwait(false);
        }

        #endregion
    }
}
