namespace Armada.Core.Database
{
    using System;
    using System.Data.Common;
    using System.Threading;
    using System.Threading.Tasks;
    using Armada.Core.Enums;
    using Armada.Core.Harbor;
    using Armada.Core.Models;

    /// <summary>Release a confirmed stopped attempt without overwriting a later captain assignment.</summary>
    internal static class HarborCaptainReleasePersistence
    {
        internal static async Task<bool> TryReleaseAsync(DbConnection connection, DatabaseTypeEnum provider,
            HarborJobSnapshot job, Captain captain, CancellationToken token)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            if (captain == null) throw new ArgumentNullException(nameof(captain));
            if (!job.StopConfirmed || !HarborJobRecord.IsTerminal(job.State)
                || captain.Id != job.CaptainId || !job.AdmiralProcessId.HasValue || !job.AdmiralProcessStartedUtc.HasValue
                || (captain.State != CaptainStateEnum.Idle && captain.State != CaptainStateEnum.Stalled
                    && captain.State != CaptainStateEnum.Quarantined))
                return false;
            await connection.OpenAsync(token).ConfigureAwait(false);
            using (IDisposable? writeLease = await Sqlite.SqliteWriteGate.EnterAsync(connection, token).ConfigureAwait(false))
            using (DbCommand command = connection.CreateCommand())
            {
                command.CommandText = @"UPDATE captains SET state = @state, current_mission_id = NULL,
                    current_dock_id = NULL, process_id = NULL, process_started_utc = NULL, recovery_attempts = CASE WHEN @state = 'Quarantined' THEN recovery_attempts ELSE 0 END,
                    quarantine_until_utc = @quarantine_until, quarantine_reason = @quarantine_reason, last_update_utc = @now
                    WHERE id = @captain_id AND tenant_id = @tenant_id AND user_id = @user_id
                    AND current_mission_id = @mission_id AND (current_dock_id IS NULL OR current_dock_id = @dock_id)
                    AND process_id = @process_id AND process_started_utc = @started AND last_update_utc = @stamp
                    AND EXISTS (SELECT 1 FROM missions WHERE id = @mission_id AND harbor_job_id = @job_id AND status = 'Pending')
                    AND EXISTS (SELECT 1 FROM harbor_jobs WHERE job_id = @job_id AND captain_id = @captain_id
                        AND mission_id = @mission_id AND dock_id = @dock_id
                        AND tenant_id = @tenant_id AND user_id = @user_id
                        AND admiral_process_id = @process_id AND admiral_process_started_utc = @job_started
                        AND stop_confirmed = 1 AND state IN ('Exited', 'Failed', 'Lost'));";
                DateTime now = DateTime.UtcNow;
                StoredValueBinder.For(provider).For(command, "captains")
                    .Text("@captain_id", "id", job.CaptainId)
                    .Text("tenant_id", job.OwnerTenantId).Text("user_id", job.OwnerUserId)
                    .Text("@mission_id", "current_mission_id", job.MissionId)
                    .Text("@dock_id", "current_dock_id", job.DockId)
                    .Text("@job_id", "id", job.JobId)
                    .Text("state", captain.State.ToString())
                    .Int("process_id", job.AdmiralProcessId)
                    .Utc("@started", "process_started_utc", job.AdmiralProcessStartedUtc)
                    .Utc("@stamp", "last_update_utc", captain.LastUpdateUtc)
                    .Utc("@quarantine_until", "quarantine_until_utc", captain.QuarantineUntilUtc)
                    .Text("quarantine_reason", captain.QuarantineReason)
                    .Utc("@now", "last_update_utc", now);
                ProductionFactSql.AddUtc(command, "@job_started", job.AdmiralProcessStartedUtc, provider);
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return false;
                captain.CurrentMissionId = null;
                captain.CurrentDockId = null;
                captain.ProcessId = null;
                if (captain.State != CaptainStateEnum.Quarantined) captain.RecoveryAttempts = 0;
                captain.LastUpdateUtc = now;
                return true;
            }
        }
    }
}
