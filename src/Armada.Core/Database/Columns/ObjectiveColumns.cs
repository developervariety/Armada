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

        /// <summary>
        /// Bind every stored objectives column, each in the form its provider stores it; list and document columns are stored as JSON.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the objectives table.</param>
        /// <param name="objective">Row to bind.</param>
        internal static void Write(StoredParameters parameters, Objective objective)
        {
            parameters
                .Text("id", objective.Id)
                .Text("tenant_id", objective.TenantId)
                .Text("user_id", objective.UserId)
                .Text("title", objective.Title)
                .Text("description", objective.Description)
                .Text("status", objective.Status.ToString())
                .Text("kind", objective.Kind.ToString())
                .Text("category", objective.Category)
                .Text("priority", objective.Priority.ToString())
                .Int("rank", objective.Rank)
                .Bool("auto_dispatch_enabled", objective.AutoDispatchEnabled)
                .Text("start_from_ref", objective.StartFromRef)
                .Text("backlog_state", objective.BacklogState.ToString())
                .Text("effort", objective.Effort.ToString())
                .Text("owner", objective.Owner)
                .Text("target_version", objective.TargetVersion)
                .Utc("due_utc", objective.DueUtc)
                .Text("parent_objective_id", objective.ParentObjectiveId)
                .Text("blocked_by_objective_ids_json", ObjectivePersistenceHelper.Serialize(objective.BlockedByObjectiveIds))
                .Text("refinement_summary", objective.RefinementSummary)
                .Text("preparation_json", ObjectivePersistenceHelper.Serialize(objective.Preparation))
                .Text("suggested_pipeline_id", objective.SuggestedPipelineId)
                .Text("suggested_playbooks_json", ObjectivePersistenceHelper.Serialize(objective.SuggestedPlaybooks))
                .Text("tags_json", ObjectivePersistenceHelper.Serialize(objective.Tags))
                .Text("acceptance_criteria_json", ObjectivePersistenceHelper.Serialize(objective.AcceptanceCriteria))
                .Text("non_goals_json", ObjectivePersistenceHelper.Serialize(objective.NonGoals))
                .Text("rollout_constraints_json", ObjectivePersistenceHelper.Serialize(objective.RolloutConstraints))
                .Text("evidence_links_json", ObjectivePersistenceHelper.Serialize(objective.EvidenceLinks))
                .Text("fleet_ids_json", ObjectivePersistenceHelper.Serialize(objective.FleetIds))
                .Text("vessel_ids_json", ObjectivePersistenceHelper.Serialize(objective.VesselIds))
                .Text("planning_session_ids_json", ObjectivePersistenceHelper.Serialize(objective.PlanningSessionIds))
                .Text("refinement_session_ids_json", ObjectivePersistenceHelper.Serialize(objective.RefinementSessionIds))
                .Text("voyage_ids_json", ObjectivePersistenceHelper.Serialize(objective.VoyageIds))
                .Text("mission_ids_json", ObjectivePersistenceHelper.Serialize(objective.MissionIds))
                .Text("check_run_ids_json", ObjectivePersistenceHelper.Serialize(objective.CheckRunIds))
                .Text("release_ids_json", ObjectivePersistenceHelper.Serialize(objective.ReleaseIds))
                .Text("deployment_ids_json", ObjectivePersistenceHelper.Serialize(objective.DeploymentIds))
                .Text("incident_ids_json", ObjectivePersistenceHelper.Serialize(objective.IncidentIds))
                .Text("source_provider", objective.SourceProvider)
                .Text("source_type", objective.SourceType)
                .Text("source_id", objective.SourceId)
                .Text("source_url", objective.SourceUrl)
                .Utc("source_updated_utc", objective.SourceUpdatedUtc)
                .Utc("created_utc", objective.CreatedUtc)
                .Utc("last_update_utc", objective.LastUpdateUtc)
                .Utc("completed_utc", objective.CompletedUtc);
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
