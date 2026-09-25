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

        /// <summary>
        /// Bind every stored merge_entries column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the merge_entries table.</param>
        /// <param name="entry">Row to bind.</param>
        internal static void Write(StoredParameters parameters, MergeEntry entry)
        {
            parameters
                .Text("id", entry.Id)
                .Text("tenant_id", entry.TenantId)
                .Text("user_id", entry.UserId)
                .Text("mission_id", entry.MissionId)
                .Text("vessel_id", entry.VesselId)
                .Text("branch_name", entry.BranchName)
                .Text("target_branch", entry.TargetBranch)
                .Text("status", entry.Status.ToString())
                .Int("priority", entry.Priority)
                .Text("batch_id", entry.BatchId)
                .Text("test_command", entry.TestCommand)
                .Text("test_output", entry.TestOutput)
                .Int("test_exit_code", entry.TestExitCode)
                .Utc("created_utc", entry.CreatedUtc)
                .Utc("last_update_utc", entry.LastUpdateUtc)
                .Utc("test_started_utc", entry.TestStartedUtc)
                .Utc("completed_utc", entry.CompletedUtc)
                .Text("audit_lane", entry.AuditLane)
                .Bool("audit_convention_passed", entry.AuditConventionPassed)
                .Text("audit_convention_notes", entry.AuditConventionNotes)
                .Text("audit_critical_trigger", entry.AuditCriticalTrigger)
                .Bool("audit_deep_picked", entry.AuditDeepPicked)
                .Utc("audit_deep_completed_utc", entry.AuditDeepCompletedUtc)
                .Text("audit_deep_verdict", entry.AuditDeepVerdict)
                .Text("audit_deep_notes", entry.AuditDeepNotes)
                .Text("audit_deep_recommended_action", entry.AuditDeepRecommendedAction)
                .Text("pr_url", entry.PrUrl)
                .Text("pr_base_branch", entry.PrBaseBranch)
                .Text("merge_failure_class", entry.MergeFailureClass?.ToString())
                .Text("conflicted_files", entry.ConflictedFiles)
                .Text("merge_failure_summary", entry.MergeFailureSummary)
                .Int("diff_line_count", entry.DiffLineCount);
        }
    }
}
