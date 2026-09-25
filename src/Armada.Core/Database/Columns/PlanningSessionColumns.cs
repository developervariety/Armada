namespace Armada.Core.Database
{
    using System.Collections.Generic;
    using System.Data;
    using System.Text.Json;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The planning_sessions row-to-model contract, shared by every provider. Every provider's schema carries every
    /// planning-session column and every read selects the whole row, so each column is read as present.
    /// </summary>
    internal static class PlanningSessionColumns
    {
        // Selected playbooks are written with the serializer defaults, so they are read with them.
        private static readonly JsonSerializerOptions _Json = new JsonSerializerOptions();

        /// <summary>
        /// Read a planning_sessions row.
        /// </summary>
        /// <param name="record">Reader positioned on a planning_sessions row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The planning session.</returns>
        internal static PlanningSession Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "PlanningSession");
            return new PlanningSession
            {
                Id = row.Text("id"),
                TenantId = row.NullableText("tenant_id"),
                UserId = row.NullableText("user_id"),
                CaptainId = row.Text("captain_id"),
                VesselId = row.Text("vessel_id"),
                FleetId = row.NullableText("fleet_id"),
                DockId = row.NullableText("dock_id"),
                BranchName = row.NullableText("branch_name"),
                Title = row.Text("title"),
                Status = row.Enum<PlanningSessionStatusEnum>("status"),
                PipelineId = row.NullableText("pipeline_id"),
                ObjectiveId = row.NullableText("objective_id"),
                SelectedPlaybooks = row.Json<List<SelectedPlaybook>>("selected_playbooks_json", _Json) ?? new List<SelectedPlaybook>(),
                ProcessId = row.NullableInt("process_id"),
                FailureReason = row.NullableText("failure_reason"),
                CreatedUtc = row.Utc("created_utc"),
                StartedUtc = row.NullableUtc("started_utc"),
                CompletedUtc = row.NullableUtc("completed_utc"),
                LastUpdateUtc = row.Utc("last_update_utc")
            };
        }
    }
}
