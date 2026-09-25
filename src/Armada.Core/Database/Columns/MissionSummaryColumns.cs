namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The mission summary row-to-model contract, shared by every provider. A summary read selects the light mission
    /// columns and the lengths of the heavy text columns, so each of those is read as present. The assignment state
    /// and review deny action tolerate names outside the model and read those as their defaults, as a full mission
    /// read does.
    /// </summary>
    internal static class MissionSummaryColumns
    {
        /// <summary>
        /// Read a mission summary row.
        /// </summary>
        /// <param name="record">Reader positioned on a mission summary row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The mission summary.</returns>
        internal static MissionSummary Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "MissionSummary");
            Mission admission = new Mission();
            MissionAdmissionPersistence.Read(record, admission);
            return new MissionSummary
            {
                LastAdmissionObservation = admission.LastAdmissionObservation,
                Id = row.Text("id"),
                TenantId = row.NullableText("tenant_id"),
                UserId = row.NullableText("user_id"),
                VoyageId = row.NullableText("voyage_id"),
                VesselId = row.NullableText("vessel_id"),
                CaptainId = row.NullableText("captain_id"),
                Title = row.Text("title"),
                Status = row.Enum<MissionStatusEnum>("status"),
                AssignmentState = row.EnumOrFallback("mission_assignment_state", MissionAssignmentStateEnum.Pending),
                Priority = row.Int("priority"),
                ParentMissionId = row.NullableText("parent_mission_id"),
                BranchName = row.NullableText("branch_name"),
                DockId = row.NullableText("dock_id"),
                ProcessId = row.NullableInt("process_id"),
                PrUrl = row.NullableText("pr_url"),
                CommitHash = row.NullableText("commit_hash"),
                Persona = row.NullableText("persona"),
                DependsOnMissionId = row.NullableText("depends_on_mission_id"),
                FailureReason = row.NullableText("failure_reason"),
                ReconciledUtc = row.NullableUtc("reconciled_utc"),
                ReconciledReason = row.NullableText("reconciled_reason"),
                RequiresReview = row.Bool("requires_review"),
                ReviewDenyAction = row.EnumOrFallback("review_deny_action", ReviewDenyActionEnum.RetryStage),
                ReviewComment = row.NullableText("review_comment"),
                ReviewedByUserId = row.NullableText("reviewed_by_user_id"),
                ReviewRequestedUtc = row.NullableUtc("review_requested_utc"),
                ReviewedUtc = row.NullableUtc("reviewed_utc"),
                DescriptionLength = row.Int("description_length"),
                DiffSnapshotLength = row.Int("diff_snapshot_length"),
                AgentOutputLength = row.Int("agent_output_length"),
                CreatedUtc = row.Utc("created_utc"),
                StartedUtc = row.NullableUtc("started_utc"),
                CompletedUtc = row.NullableUtc("completed_utc"),
                TotalRuntimeMs = row.NullableLong("total_runtime_ms"),
                LastUpdateUtc = row.Utc("last_update_utc")
            };
        }
    }
}
