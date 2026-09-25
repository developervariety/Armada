namespace Armada.Core.Database
{
    using System.Collections.Generic;
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The objectives row-to-model contract, shared by every provider. Every stored value that cannot be read
    /// raises <see cref="StoredObjectiveDataException"/> naming the objective and the field, so a list read skips
    /// and reports that one row instead of failing, and no field is read as an empty or default value in place of
    /// the stored one. A blank enum or JSON column reads as the model default.
    /// </summary>
    internal static class ObjectiveColumns
    {
        /// <summary>
        /// Read an objectives row.
        /// </summary>
        /// <param name="record">Reader positioned on an objectives row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The objective.</returns>
        internal static Objective Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "Objective");
            string id = row.Text("id");
            try
            {
                return ReadRow(row, id);
            }
            catch (StoredRowException ex)
            {
                throw new StoredObjectiveDataException(id, ex.Column, "holds a value that cannot be read on " + values.Provider, ex);
            }
        }

        private static Objective ReadRow(StoredRow row, string id)
        {
            Objective objective = new Objective
            {
                Id = id,
                TenantId = row.NullableText("tenant_id"),
                UserId = row.NullableText("user_id"),
                Title = row.Text("title"),
                Description = row.NullableText("description"),
                Status = ObjectivePersistenceHelper.ParseObjectiveEnum(row.TextOrNull("status"), ObjectiveStatusEnum.Draft, id, "status"),
                Kind = ObjectivePersistenceHelper.ParseObjectiveEnum(row.TextOrNull("kind"), ObjectiveKindEnum.Feature, id, "kind"),
                Category = row.NullableText("category"),
                Priority = ObjectivePersistenceHelper.ParseObjectiveEnum(row.TextOrNull("priority"), ObjectivePriorityEnum.P2, id, "priority"),
                Rank = row.NullableInt("rank") ?? 0,
                AutoDispatchEnabled = row.NullableBool("auto_dispatch_enabled") ?? false,
                BacklogState = ObjectivePersistenceHelper.ParseObjectiveEnum(row.TextOrNull("backlog_state"), ObjectiveBacklogStateEnum.Inbox, id, "backlog_state"),
                Effort = ObjectivePersistenceHelper.ParseObjectiveEnum(row.TextOrNull("effort"), ObjectiveEffortEnum.M, id, "effort"),
                Owner = row.NullableText("owner"),
                TargetVersion = row.NullableText("target_version"),
                DueUtc = row.NullableUtc("due_utc"),
                ParentObjectiveId = row.NullableText("parent_objective_id"),
                RefinementSummary = row.NullableText("refinement_summary"),
                StartFromRef = row.NullableText("start_from_ref"),
                SuggestedPipelineId = row.NullableText("suggested_pipeline_id"),
                SourceProvider = row.NullableText("source_provider"),
                SourceType = row.NullableText("source_type"),
                SourceId = row.NullableText("source_id"),
                SourceUrl = row.NullableText("source_url"),
                SourceUpdatedUtc = row.NullableUtc("source_updated_utc"),
                CreatedUtc = row.Utc("created_utc"),
                LastUpdateUtc = row.Utc("last_update_utc"),
                CompletedUtc = row.NullableUtc("completed_utc")
            };

            objective.BlockedByObjectiveIds = List(row, id, "blocked_by_objective_ids_json");
            objective.Preparation = ObjectivePersistenceHelper.DeserializePreparation(row.TextOrNull("preparation_json"), id, "preparation_json");
            objective.SuggestedPlaybooks = ObjectivePersistenceHelper.DeserializePlaybooks(row.TextOrNull("suggested_playbooks_json"), id, "suggested_playbooks_json");
            objective.Tags = List(row, id, "tags_json");
            objective.AcceptanceCriteria = List(row, id, "acceptance_criteria_json");
            objective.NonGoals = List(row, id, "non_goals_json");
            objective.RolloutConstraints = List(row, id, "rollout_constraints_json");
            objective.EvidenceLinks = List(row, id, "evidence_links_json");
            objective.FleetIds = List(row, id, "fleet_ids_json");
            objective.VesselIds = List(row, id, "vessel_ids_json");
            objective.PlanningSessionIds = List(row, id, "planning_session_ids_json");
            objective.RefinementSessionIds = List(row, id, "refinement_session_ids_json");
            objective.VoyageIds = List(row, id, "voyage_ids_json");
            objective.MissionIds = List(row, id, "mission_ids_json");
            objective.CheckRunIds = List(row, id, "check_run_ids_json");
            objective.ReleaseIds = List(row, id, "release_ids_json");
            objective.DeploymentIds = List(row, id, "deployment_ids_json");
            objective.IncidentIds = List(row, id, "incident_ids_json");
            objective.NormalizeTenancy();
            return objective;
        }

        private static List<string> List(StoredRow row, string id, string column)
        {
            return ObjectivePersistenceHelper.DeserializeList(row.TextOrNull(column), id, column);
        }
    }
}
