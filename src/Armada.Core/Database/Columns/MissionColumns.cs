namespace Armada.Core.Database
{
    using System.Collections.Generic;
    using System.Data;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The missions row-to-model contract, shared by every provider. Every provider's schema carries every mission
    /// column and every mission read selects the whole row, so each column is read as present. The process
    /// identifier is read before its start time, because a new identifier clears the start time on the model, and
    /// the start and completion times are read before the stored runtime, because setting either recomputes it.
    /// </summary>
    internal static class MissionColumns
    {
        // Prestaged files are written with the serializer defaults, so they are read with them.
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions();

        /// <summary>
        /// Read a missions row.
        /// </summary>
        /// <param name="record">Reader positioned on a missions row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The mission.</returns>
        internal static Mission Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Mission");
            Mission mission = new Mission();
            MissionAdmissionPersistence.Read(record, mission);
            mission.Id = row.Text("id");
            mission.TenantId = row.NullableText("tenant_id");
            mission.UserId = row.NullableText("user_id");
            mission.VoyageId = row.NullableText("voyage_id");
            mission.VesselId = row.NullableText("vessel_id");
            mission.CaptainId = row.NullableText("captain_id");
            mission.Title = row.Text("title");
            mission.Description = row.NullableText("description");
            mission.Status = row.Enum<MissionStatusEnum>("status");
            mission.AssignmentState = row.EnumOrFallback("mission_assignment_state", MissionAssignmentStateEnum.Pending);
            mission.Priority = row.Int("priority");
            mission.ParentMissionId = row.NullableText("parent_mission_id");
            mission.BranchName = row.NullableText("branch_name");
            mission.DockId = row.NullableText("dock_id");
            mission.ProcessId = row.NullableInt("process_id");
            mission.ProcessStartedUtc = row.NullableUtc("process_started_utc");
            mission.PrUrl = row.NullableText("pr_url");
            mission.CommitHash = row.NullableText("commit_hash");
            mission.DiffSnapshot = row.NullableText("diff_snapshot");
            mission.AgentOutput = row.NullableText("agent_output");
            mission.Persona = row.NullableText("persona");
            mission.RequestedCaptainId = row.NullableText("requested_captain_id");
            mission.Tier = row.NullableEnum<CaptainTierEnum>("tier");
            mission.DependsOnMissionId = row.NullableText("depends_on_mission_id");
            mission.StageOrder = row.NullableInt("stage_order");
            mission.PreferredModel = row.NullableText("preferred_model");
            mission.CapabilityHint = row.NullableText("capabilityhint");
            mission.Mode = MissionModes.Parse(row.NullableText("mission_mode"));
            mission.FailureReason = row.NullableText("failure_reason");
            mission.ReconciledUtc = row.NullableUtc("reconciled_utc");
            mission.ReconciledReason = row.NullableText("reconciled_reason");
            mission.HeldForOperatorReview = row.Bool(MissionOperatorHoldPersistence.HeldColumn);
            mission.HeldForOperatorReviewReason = mission.HeldForOperatorReview ? row.TextOrNull(MissionOperatorHoldPersistence.ReasonColumn) : null;
            mission.RequiresReview = row.Bool("requires_review");
            mission.ReviewDenyAction = row.EnumOrFallback("review_deny_action", ReviewDenyActionEnum.RetryStage);
            mission.ReviewComment = row.NullableText("review_comment");
            mission.ReviewedByUserId = row.NullableText("reviewed_by_user_id");
            mission.ReviewRequestedUtc = row.NullableUtc("review_requested_utc");
            mission.ReviewedUtc = row.NullableUtc("reviewed_utc");
            mission.PrestagedFiles = PrestagedFiles(row);
            mission.CreatedUtc = row.Utc("created_utc");
            mission.StartedUtc = row.NullableUtc("started_utc");
            mission.CompletedUtc = row.NullableUtc("completed_utc");
            mission.TotalRuntimeMs = row.NullableLong("total_runtime_ms");
            mission.LastUpdateUtc = row.Utc("last_update_utc");
            mission.RecoveryAttempts = row.Int("recovery_attempts");
            mission.LandingRetryCount = row.Int("landing_retry_count");
            mission.StartFromRef = row.TextOrNull("start_from_ref");
            mission.RetrySkipCaptainIds = row.NullableText("retry_skip_captain_ids");
            mission.LastRecoveryActionUtc = row.NullableUtc("last_recovery_action_utc");
            return mission;
        }

        /// <summary>
        /// Prestaged files are stored as a JSON list; an empty list reads as none, as it is written.
        /// </summary>
        private static List<PrestagedFile>? PrestagedFiles(StoredRow row)
        {
            List<PrestagedFile>? files = row.Json<List<PrestagedFile>>("prestaged_files", _Json);
            return files != null && files.Count > 0 ? files : null;
        }
    }
}
