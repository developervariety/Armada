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
    }
}
