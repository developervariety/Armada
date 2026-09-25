namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The merge_entries row-to-model contract, shared by every provider. Every provider's schema carries every
    /// merge-entry column and every merge-entry read selects the whole row, so each column is read as present.
    /// The audit, pull-request and failure-detail text columns read verbatim, and a failure class outside the
    /// model reads as unclassified.
    /// </summary>
    internal static class MergeEntryColumns
    {
        /// <summary>
        /// Read a merge_entries row.
        /// </summary>
        /// <param name="record">Reader positioned on a merge_entries row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The merge entry.</returns>
        internal static MergeEntry Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "MergeEntry");
            MergeEntry entry = new MergeEntry();
            entry.Id = row.Text("id");
            entry.TenantId = row.NullableText("tenant_id");
            entry.UserId = row.NullableText("user_id");
            entry.MissionId = row.NullableText("mission_id");
            entry.VesselId = row.NullableText("vessel_id");
            entry.BranchName = row.Text("branch_name");
            entry.TargetBranch = row.Text("target_branch");
            entry.Status = row.Enum<MergeStatusEnum>("status");
            entry.Priority = row.Int("priority");
            entry.BatchId = row.NullableText("batch_id");
            entry.TestCommand = row.NullableText("test_command");
            entry.TestOutput = row.NullableText("test_output");
            entry.TestExitCode = row.NullableInt("test_exit_code");
            entry.CreatedUtc = row.Utc("created_utc");
            entry.LastUpdateUtc = row.Utc("last_update_utc");
            entry.TestStartedUtc = row.NullableUtc("test_started_utc");
            entry.CompletedUtc = row.NullableUtc("completed_utc");
            entry.AuditLane = row.TextOrNull("audit_lane");
            entry.AuditConventionPassed = row.NullableBool("audit_convention_passed");
            entry.AuditConventionNotes = row.TextOrNull("audit_convention_notes");
            entry.AuditCriticalTrigger = row.TextOrNull("audit_critical_trigger");
            entry.AuditDeepPicked = row.NullableBool("audit_deep_picked");
            entry.AuditDeepCompletedUtc = row.NullableUtc("audit_deep_completed_utc");
            entry.AuditDeepVerdict = row.TextOrNull("audit_deep_verdict");
            entry.AuditDeepNotes = row.TextOrNull("audit_deep_notes");
            entry.AuditDeepRecommendedAction = row.TextOrNull("audit_deep_recommended_action");
            entry.PrUrl = row.TextOrNull("pr_url");
            entry.PrBaseBranch = row.TextOrNull("pr_base_branch");
            entry.MergeFailureClass = row.EnumOrNull<MergeFailureClassEnum>("merge_failure_class", false);
            entry.ConflictedFiles = row.TextOrNull("conflicted_files");
            entry.MergeFailureSummary = row.TextOrNull("merge_failure_summary");
            entry.DiffLineCount = row.Int("diff_line_count");
            return entry;
        }
    }
}
