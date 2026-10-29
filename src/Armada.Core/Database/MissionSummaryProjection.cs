namespace Armada.Core.Database
{
    /// <summary>
    /// The select list for Mission-shaped summary reads on every provider. It names every mission column the
    /// row mappers read, with the heavy text columns (description, diff snapshot, agent output) selected as NULL,
    /// so a summary never transfers them from the database and the full-row mapper still finds every column.
    /// A column added to the missions table and its mapper belongs here too, or summary reads return its default.
    /// </summary>
    internal static class MissionSummaryProjection
    {
        /// <summary>
        /// Summary select list, valid SQL on SQLite, PostgreSQL, MySQL and SQL Server.
        /// </summary>
        internal const string Columns =
            "id, tenant_id, user_id, voyage_id, vessel_id, captain_id, title, NULL AS description, status, " +
            "mission_assignment_state, priority, parent_mission_id, branch_name, dock_id, dock_provision_attempt_id, harbor_job_id, process_id, process_started_utc, " +
            "pr_url, commit_hash, NULL AS diff_snapshot, NULL AS agent_output, persona, depends_on_mission_id, stage_order, stage_chain_id, " +
            "failure_reason, reconciled_utc, reconciled_reason, total_runtime_ms, prestaged_files, preferred_model, " +
            "capabilityhint, mission_mode, requires_review, review_deny_action, review_comment, reviewed_by_user_id, " +
            "review_requested_utc, reviewed_utc, recovery_attempts, landing_retry_count, start_from_ref, " +
            "last_recovery_action_utc, created_utc, started_utc, completed_utc, last_update_utc, retry_skip_captain_ids, " +
            "NULL AS preserved_run_output, research_replay_id, " +
            "tier, requested_captain_id, held_for_operator_review, held_for_operator_review_reason, is_evaluation, " +
            "assignment_hold_reason, assignment_hold_entity_id, " +
            "last_admission_json, admission_revision, integration_barrier_json, integration_barrier_revision";
    }
}
