namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The planning_session_messages row-to-model contract, shared by every provider. Every read selects the whole
    /// row, so each column is read as present. Null content reads as empty text.
    /// </summary>
    internal static class PlanningSessionMessageColumns
    {
        /// <summary>
        /// Read a planning_session_messages row.
        /// </summary>
        /// <param name="record">Reader positioned on a planning_session_messages row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The planning-session message.</returns>
        internal static PlanningSessionMessage Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "PlanningSessionMessage");
            return new PlanningSessionMessage
            {
                Id = row.Text("id"),
                PlanningSessionId = row.Text("planning_session_id"),
                TenantId = row.NullableText("tenant_id"),
                UserId = row.NullableText("user_id"),
                Role = row.Text("role"),
                Sequence = row.Int("sequence"),
                Content = row.Text("content"),
                IsSelectedForDispatch = row.Bool("is_selected_for_dispatch"),
                CreatedUtc = row.Utc("created_utc"),
                LastUpdateUtc = row.Utc("last_update_utc")
            };
        }
    }
}
