namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The landing_jobs row-to-model contract, shared by every provider. Optional text reads verbatim, so an
    /// empty stored error stays empty.
    /// </summary>
    internal static class LandingJobColumns
    {
        /// <summary>
        /// Read a landing_jobs row.
        /// </summary>
        /// <param name="record">Reader positioned on a landing_jobs row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The landing job.</returns>
        internal static LandingJob Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "LandingJob");
            LandingJob job = new LandingJob();
            job.Id = row.Text("id");
            job.TenantId = row.TextOrNull("tenant_id");
            job.UserId = row.TextOrNull("user_id");
            job.MergeEntryId = row.Text("merge_entry_id");
            job.MissionId = row.TextOrNull("mission_id");
            job.VesselId = row.TextOrNull("vessel_id");
            job.BranchName = row.Text("branch_name");
            job.TargetBranch = row.Text("target_branch");
            job.State = row.Enum<LandingJobStateEnum>("state");
            job.RetryCount = row.Int("retry_count");
            job.CreatedUtc = row.Utc("created_utc");
            job.LastUpdateUtc = row.Utc("last_update_utc");
            job.StartedUtc = row.NullableUtc("started_utc");
            job.CompletedUtc = row.NullableUtc("completed_utc");
            job.LastError = row.TextOrNull("last_error");
            return job;
        }

        /// <summary>
        /// Bind every stored landing_jobs column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the landing_jobs table.</param>
        /// <param name="job">Row to bind.</param>
        internal static void Write(StoredParameters parameters, LandingJob job)
        {
            parameters
                .Text("id", job.Id)
                .Text("tenant_id", job.TenantId)
                .Text("user_id", job.UserId)
                .Text("merge_entry_id", job.MergeEntryId)
                .Text("mission_id", job.MissionId)
                .Text("vessel_id", job.VesselId)
                .Text("branch_name", job.BranchName)
                .Text("target_branch", job.TargetBranch)
                .Text("state", job.State.ToString())
                .Int("retry_count", job.RetryCount)
                .Utc("created_utc", job.CreatedUtc)
                .Utc("last_update_utc", job.LastUpdateUtc)
                .Utc("started_utc", job.StartedUtc)
                .Utc("completed_utc", job.CompletedUtc)
                .Text("last_error", job.LastError);
        }
    }
}
