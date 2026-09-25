namespace Armada.Core.Database
{
    using System.Data;
    using Armada.Core.Enums;
    using Armada.Core.Models;

    /// <summary>
    /// The coordination_messages row-to-model contract, shared by every provider that stores the coordination board. Every read
    /// selects the whole row, so each column is read as present.
    /// </summary>
    internal static class CoordinationMessageColumns
    {
        /// <summary>
        /// Read a coordination_messages row.
        /// </summary>
        /// <param name="record">Reader positioned on a coordination_messages row.</param>
        /// <param name="values">Provider value converter.</param>
        /// <returns>The coordination message.</returns>
        internal static CoordinationMessage Read(IDataRecord record, StoredValueConverter values)
        {
            StoredRow row = new StoredRow(record, values, "CoordinationMessage");
            return new CoordinationMessage
            {
                Id = row.Text("id"),
                CoordinationRoomId = row.Text("coordination_room_id"),
                TenantId = row.NullableText("tenant_id"),
                AuthorType = row.Enum<CoordinationAuthorTypeEnum>("author_type"),
                AuthorId = row.NullableText("author_id"),
                AuthorName = row.Text("author_name"),
                Content = row.Text("content"),
                VoyageId = row.NullableText("voyage_id"),
                MissionId = row.NullableText("mission_id"),
                VesselId = row.NullableText("vessel_id"),
                IncidentId = row.NullableText("incident_id"),
                ToParticipantKey = row.NullableText("to_participant_key"),
                CreatedUtc = row.Utc("created_utc"),
                LastUpdateUtc = row.Utc("last_update_utc")
            };
        }

        /// <summary>
        /// Bind every stored coordination_messages column, each in the form its provider stores it.
        /// </summary>
        /// <param name="parameters">Parameters of a command addressing the coordination_messages table.</param>
        /// <param name="message">Row to bind.</param>
        internal static void Write(StoredParameters parameters, CoordinationMessage message)
        {
            parameters
                .Text("id", message.Id)
                .Text("coordination_room_id", message.CoordinationRoomId)
                .Text("tenant_id", message.TenantId)
                .Text("author_type", message.AuthorType.ToString())
                .Text("author_id", message.AuthorId)
                .Text("author_name", message.AuthorName)
                .Text("content", message.Content)
                .Text("voyage_id", message.VoyageId)
                .Text("mission_id", message.MissionId)
                .Text("vessel_id", message.VesselId)
                .Text("incident_id", message.IncidentId)
                .Text("to_participant_key", message.ToParticipantKey)
                .Utc("created_utc", message.CreatedUtc)
                .Utc("last_update_utc", message.LastUpdateUtc);
        }
    }
}
