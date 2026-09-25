namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The objective_refinement_sessions row-to-model contract, shared by every provider. Every stored value that
    /// cannot be read raises <see cref="StoredRefinementSessionDataException"/> naming the session and the field, so
    /// a list read skips and reports that one row instead of failing. A blank status reads as Created.
    /// </summary>
    internal static class ObjectiveRefinementSessionColumns
    {
        /// <summary>
        /// Read an objective_refinement_sessions row.
        /// </summary>
        /// <param name="record">Reader positioned on an objective_refinement_sessions row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The refinement session.</returns>
        internal static ObjectiveRefinementSession Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "ObjectiveRefinementSession");
            string id = row.Text("id");
            try
            {
                return new ObjectiveRefinementSession
                {
                    Id = id,
                    ObjectiveId = row.Text("objective_id"),
                    TenantId = row.NullableText("tenant_id"),
                    UserId = row.NullableText("user_id"),
                    CaptainId = row.Text("captain_id"),
                    FleetId = row.NullableText("fleet_id"),
                    VesselId = row.NullableText("vessel_id"),
                    Title = row.Text("title"),
                    Status = RefinementSessionPersistenceHelper.ParseStatus(row.TextOrNull(RefinementSessionPersistenceHelper.StatusField), id),
                    ProcessId = row.NullableInt("process_id"),
                    FailureReason = row.NullableText("failure_reason"),
                    CreatedUtc = row.Utc("created_utc"),
                    StartedUtc = row.NullableUtc("started_utc"),
                    CompletedUtc = row.NullableUtc("completed_utc"),
                    LastUpdateUtc = row.Utc("last_update_utc")
                };
            }
            catch (StoredRowException ex)
            {
                throw new StoredRefinementSessionDataException(id, ex.Column, "holds a value that cannot be read on " + values.Provider, ex);
            }
        }
    }
}
