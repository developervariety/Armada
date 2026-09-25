namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Models;

    /// <summary>
    /// The objective_refinement_messages row-to-model contract, shared by every provider. Every provider's schema
    /// carries every message column and every message read selects the whole row, so each column is read as
    /// present. Null content reads as empty text.
    /// </summary>
    internal static class ObjectiveRefinementMessageColumns
    {
        /// <summary>
        /// Read an objective_refinement_messages row.
        /// </summary>
        /// <param name="record">Reader positioned on an objective_refinement_messages row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The refinement message.</returns>
        internal static ObjectiveRefinementMessage Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "ObjectiveRefinementMessage");
            return new ObjectiveRefinementMessage
            {
                Id = row.Text("id"),
                ObjectiveRefinementSessionId = row.Text("objective_refinement_session_id"),
                ObjectiveId = row.Text("objective_id"),
                TenantId = row.NullableText("tenant_id"),
                UserId = row.NullableText("user_id"),
                Role = row.Text("role"),
                Sequence = row.Int("sequence"),
                Content = row.Text("content"),
                IsSelected = row.Bool("is_selected"),
                CreatedUtc = row.Utc("created_utc"),
                LastUpdateUtc = row.Utc("last_update_utc")
            };
        }

        /// <summary>
        /// Bind every stored objective_refinement_messages column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the objective_refinement_messages table.</param>
        /// <param name="message">Row to bind.</param>
        internal static void Write(StoredParameters parameters, ObjectiveRefinementMessage message)
        {
            parameters
                .Text("id", message.Id)
                .Text("objective_refinement_session_id", message.ObjectiveRefinementSessionId)
                .Text("objective_id", message.ObjectiveId)
                .Text("tenant_id", message.TenantId)
                .Text("user_id", message.UserId)
                .Text("role", message.Role)
                .Int("sequence", message.Sequence)
                .Text("content", message.Content)
                .Bool("is_selected", message.IsSelected)
                .Utc("created_utc", message.CreatedUtc)
                .Utc("last_update_utc", message.LastUpdateUtc);
        }
    }
}
